'use strict';

const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { ModelManager } = require('./src/main/models');
const pipeline = require('./src/main/pipeline');
const urlfetch = require('./src/main/urlfetch');

let win = null;
let modelManager = null;

const userDir = () => app.getPath('userData');
const settingsPath = () => path.join(userDir(), 'settings.json');
const transcriptsDir = () => path.join(userDir(), 'transcripts');
const toolsDir = () => path.join(userDir(), 'tools');
const mediaDir = () => path.join(userDir(), 'media');

const DEFAULT_SETTINGS = { model: 'whisper-base', language: '', numSpeakers: 0 };

function loadSettings() {
  try {
    return { ...DEFAULT_SETTINGS, ...JSON.parse(fs.readFileSync(settingsPath(), 'utf8')) };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

function saveSettings(s) {
  const tmp = `${settingsPath()}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(s, null, 2));
  fs.renameSync(tmp, settingsPath());
}

function safeFilename(name) {
  let s = String(name || 'transcript').replace(/[\\/:*?"<>|\x00-\x1f]/g, '_').trim();
  if (/^(CON|PRN|AUX|NUL|COM\d|LPT\d)$/i.test(s.split('.')[0])) s = `_${s}`;
  return s || 'transcript';
}

function binDir() {
  if (app.isPackaged) return path.join(process.resourcesPath, 'vendor', 'bin');
  return path.join(app.getAppPath(), 'vendor', process.platform === 'win32' ? 'win' : 'mac', 'bin');
}

// ---------------------------------------------------------------- library --
function libraryList() {
  const dir = transcriptsDir();
  let files = [];
  try { files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')); } catch { return []; }
  const entries = [];
  for (const f of files) {
    try {
      const p = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
      if (!p || !Array.isArray(p.segments)) throw new Error('not a transcript');
      entries.push({
        id: p.id,
        title: p.title,
        audioName: p.audioName,
        audioPath: p.audioPath,
        durationSec: p.durationSec,
        createdAt: p.createdAt,
        updatedAt: p.updatedAt,
        speakerNames: Object.values(p.speakers || {}).map((s) => s.name),
        snippet: (p.segments && p.segments[0] && p.segments[0].text || '').slice(0, 160)
      });
    } catch {
      // Move corrupt files aside instead of silently hiding them forever.
      console.warn('Skipping unreadable transcript', f);
      fs.promises.rename(path.join(dir, f), path.join(dir, `${f}.corrupt`)).catch(() => {});
    }
  }
  entries.sort((a, b) => (b.updatedAt || '').localeCompare(a.updatedAt || ''));
  return entries;
}

const validProjectId = (id) => typeof id === 'string' && /^[a-f0-9]{16}$/.test(id);

// Set of audio paths referenced by any saved transcript (resolved).
function referencedAudioPaths(excludeId) {
  const refs = new Set();
  let files = [];
  try { files = fs.readdirSync(transcriptsDir()).filter((f) => f.endsWith('.json')); } catch { return refs; }
  for (const f of files) {
    if (excludeId && f === `${excludeId}.json`) continue;
    try {
      const p = JSON.parse(fs.readFileSync(path.join(transcriptsDir(), f), 'utf8'));
      if (p && typeof p.audioPath === 'string' && p.audioPath) refs.add(path.resolve(p.audioPath));
    } catch { /* unreadable transcript */ }
  }
  return refs;
}

const inMediaDir = (p) => !!p && path.resolve(String(p)).startsWith(mediaDir() + path.sep);

// Deletes a file in OUR media dir, but only when no saved transcript still
// points at it (exports/imports can share one fetched file).
async function discardMediaIfUnreferenced(p, excludeId) {
  if (!inMediaDir(p)) return;
  const resolved = path.resolve(String(p));
  if (referencedAudioPaths(excludeId).has(resolved)) return;
  await fs.promises.rm(resolved, { force: true }).catch(() => {});
}

// Startup sweep: fetched audio that lost its transcript (cancelled runs,
// crashes) plus downloader journal files must not pile up invisibly.
function sweepMediaDir() {
  let files = [];
  try { files = fs.readdirSync(mediaDir()); } catch { return; }
  const refs = referencedAudioPaths();
  for (const f of files) {
    const full = path.join(mediaDir(), f);
    if (f.endsWith('.part') || f.endsWith('.ytdl') || !refs.has(path.resolve(full))) {
      fs.promises.rm(full, { force: true }).catch(() => {});
    }
  }
}

function projectFile(id) {
  if (!validProjectId(id)) throw new Error('Bad project id');
  return path.join(transcriptsDir(), `${id}.json`);
}

// ------------------------------------------------------------ transcription --
const jobState = { signal: null, child: null, tempDir: null };
const urlJob = { signal: null, child: null };

async function handleTranscribe(_e, { pcm, options }) {
  if (jobState.signal) throw new Error('A transcription is already running');
  const signal = { cancelled: false };
  jobState.signal = signal;
  const sendProgress = (p) => {
    if (win && !win.isDestroyed()) win.webContents.send('transcribe:progress', p);
  };
  const tempDir = path.join(app.getPath('temp'), `verbatim-${process.pid}-${Date.now()}`);
  jobState.tempDir = tempDir;
  try {
    fs.mkdirSync(tempDir, { recursive: true });

    // Make sure models exist (first run downloads them, with UI progress).
    await modelManager.ensure(options.model, (p) => {
      if (win && !win.isDestroyed()) win.webContents.send('models:progress', p);
    }, signal);

    const result = await pipeline.transcribe({
      pcm: new Int16Array(pcm),
      binDir: binDir(),
      models: {
        segmentation: modelManager.segmentationPath(),
        embedding: modelManager.embeddingPath(),
        whisper: modelManager.whisperPaths(options.model)
      },
      tempDir,
      language: options.language || '',
      numSpeakers: options.numSpeakers || 0,
      onProgress: sendProgress,
      signal,
      track: (child) => { jobState.child = child; }
    });
    return result;
  } finally {
    jobState.signal = null;
    jobState.child = null;
    fs.promises.rm(tempDir, { recursive: true, force: true }).catch(() => {});
  }
}

// ------------------------------------------------------------------- ipc --
function registerIpc() {
  ipcMain.handle('state:get', async () => ({
    settings: loadSettings(),
    models: modelManager.listWhisper(),
    diarizationReady: modelManager.diarizationReady(),
    library: libraryList(),
    platform: process.platform,
    version: app.getVersion(),
    testAudioPath: (!app.isPackaged && process.env.VERBATIM_TEST_AUDIO) || null,
    mediaDir: mediaDir(),
    modelsDir: path.join(userDir(), 'models'),
    modelStorageBytes: await modelManager.storageBytes()
  }));

  ipcMain.handle('settings:set', (_e, partial) => {
    const s = { ...loadSettings(), ...partial };
    saveSettings(s);
    return s;
  });

  ipcMain.handle('transcribe:run', handleTranscribe);

  ipcMain.handle('transcribe:cancel', () => {
    if (jobState.signal) {
      jobState.signal.cancelled = true;
      if (jobState.signal.abort) jobState.signal.abort(); // stop an in-flight model download now
    }
    if (jobState.child) {
      try { jobState.child.kill('SIGKILL'); } catch { /* already gone */ }
    }
    return true;
  });

  // Only media (for playback of a project's linked audio) and JSON projects —
  // never a general file-read primitive.
  const READABLE = new Set(['mp3', 'wav', 'm4a', 'aac', 'flac', 'ogg', 'oga', 'opus',
    'webm', 'weba', 'mp4', 'm4b', 'mkv', 'mka', 'mov', 'flv', 'ts', 'aiff', 'aif',
    'caf', 'wma', 'amr', '3gp', 'json']);
  ipcMain.handle('file:read', async (_e, filePath) => {
    const ext = path.extname(String(filePath)).slice(1).toLowerCase();
    if (!READABLE.has(ext)) throw new Error('Unsupported file type');
    const st = await fs.promises.stat(filePath);
    if (st.size > 2.5e9) throw new Error('File is too large');
    const buf = await fs.promises.readFile(filePath);
    return buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  });

  ipcMain.handle('file:saveText', async (_e, { defaultName, content, filterName, filterExt }) => {
    const r = await dialog.showSaveDialog(win, {
      defaultPath: defaultName,
      filters: [{ name: filterName, extensions: [filterExt] }]
    });
    if (r.canceled || !r.filePath) return null;
    await fs.promises.writeFile(r.filePath, content, 'utf8');
    return r.filePath;
  });

  ipcMain.handle('project:save', async (_e, project) => {
    fs.mkdirSync(transcriptsDir(), { recursive: true });
    if (!validProjectId(project.id)) project.id = crypto.randomBytes(8).toString('hex');
    project.updatedAt = new Date().toISOString();
    if (!project.createdAt) project.createdAt = project.updatedAt;
    await fs.promises.writeFile(projectFile(project.id), JSON.stringify(project, null, 1), 'utf8');
    return { id: project.id, createdAt: project.createdAt, updatedAt: project.updatedAt };
  });

  ipcMain.handle('project:load', async (_e, id) => {
    return JSON.parse(await fs.promises.readFile(projectFile(id), 'utf8'));
  });

  ipcMain.handle('project:delete', async (_e, id) => {
    const file = projectFile(id);
    // Audio fetched from a URL lives in our media dir and belongs to its
    // transcript(s) — remove it when the last one goes. User files are never
    // touched.
    let audioPath = null;
    try {
      audioPath = JSON.parse(await fs.promises.readFile(file, 'utf8')).audioPath || null;
    } catch { /* transcript unreadable — still delete it */ }
    await fs.promises.rm(file, { force: true });
    if (audioPath) await discardMediaIfUnreferenced(audioPath, id);
    return libraryList();
  });

  ipcMain.handle('project:exportFile', async (_e, project) => {
    const r = await dialog.showSaveDialog(win, {
      defaultPath: `${safeFilename(project.title)}.verbatim.json`,
      filters: [{ name: 'Verbatim project', extensions: ['json'] }]
    });
    if (r.canceled || !r.filePath) return null;
    await fs.promises.writeFile(r.filePath, JSON.stringify(project, null, 1), 'utf8');
    return r.filePath;
  });

  ipcMain.handle('project:openDialog', async () => {
    const r = await dialog.showOpenDialog(win, {
      properties: ['openFile'],
      filters: [{ name: 'Verbatim project', extensions: ['json'] }]
    });
    if (r.canceled || !r.filePaths[0]) return null;
    return JSON.parse(await fs.promises.readFile(r.filePaths[0], 'utf8'));
  });

  ipcMain.handle('shell:showFolder', (_e, p) => shell.showItemInFolder(p));
  ipcMain.handle('shell:openModelsFolder', () => shell.openPath(path.join(userDir(), 'models')));

  // ------------------------------------------------------------ url fetch --
  ipcMain.handle('url:fetch', async (_e, url) => {
    if (urlJob.signal) throw new Error('A link is already being fetched');
    const signal = { cancelled: false };
    urlJob.signal = signal;
    try {
      return await urlfetch.fetchUrl({
        url,
        toolsDir: toolsDir(),
        mediaDir: mediaDir(),
        signal,
        track: (child) => { urlJob.child = child; },
        onProgress: (p) => {
          if (win && !win.isDestroyed()) win.webContents.send('url:progress', p);
        }
      });
    } finally {
      urlJob.signal = null;
      urlJob.child = null;
    }
  });

  ipcMain.handle('url:cancel', () => {
    if (urlJob.signal) {
      urlJob.signal.cancelled = true;
      if (urlJob.signal.abort) urlJob.signal.abort();
    }
    urlfetch.killTree(urlJob.child);
    return true;
  });

  ipcMain.handle('url:toolInfo', async () => ({
    version: await urlfetch.toolVersion(toolsDir())
  }));

  // Shares the urlJob lock with url:fetch so Update can never race a fetch's
  // first-use download of the same binary (two writers on one .part file).
  ipcMain.handle('url:toolUpdate', async () => {
    if (urlJob.signal) throw new Error('A link is being fetched — try again in a moment');
    const signal = { cancelled: false };
    urlJob.signal = signal;
    try {
      return await urlfetch.updateTool(toolsDir(), (p) => {
        if (win && !win.isDestroyed()) win.webContents.send('url:progress', p);
      }, signal, (child) => { urlJob.child = child; });
    } finally {
      urlJob.signal = null;
      urlJob.child = null;
    }
  });

  // Lets the renderer drop a fetched file whose transcription never became a
  // saved project. Restricted to our own media dir and refcounted against the
  // library, like project:delete.
  ipcMain.handle('media:discard', async (_e, p) => {
    await discardMediaIfUnreferenced(p);
    return true;
  });
}

// ---------------------------------------------------------------- window --
function createWindow() {
  win = new BrowserWindow({
    width: 1180,
    height: 800,
    minWidth: 880,
    minHeight: 600,
    backgroundColor: '#0e1116',
    show: false,
    titleBarStyle: process.platform === 'darwin' ? 'hiddenInset' : 'default',
    webPreferences: {
      preload: path.join(__dirname, 'src', 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false
    }
  });
  win.loadFile(path.join(__dirname, 'src', 'renderer', 'index.html'));
  win.once('ready-to-show', () => win.show());
  win.on('closed', () => {
    win = null;
    // A job whose window is gone can neither report nor be cancelled — stop it.
    for (const job of [jobState, urlJob]) {
      if (job.signal) {
        job.signal.cancelled = true;
        if (job.signal.abort) job.signal.abort();
      }
    }
    if (jobState.child) {
      try { jobState.child.kill('SIGKILL'); } catch { /* already gone */ }
    }
    urlfetch.killTree(urlJob.child);
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore();
      win.focus();
    }
  });
}

app.whenReady().then(() => {
  modelManager = new ModelManager(path.join(userDir(), 'models'));
  registerIpc();
  sweepMediaDir();
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

// Don't leave transcriber processes or temp wavs behind if the user quits
// mid-transcription.
app.on('before-quit', () => {
  for (const job of [jobState, urlJob]) {
    if (job.signal) job.signal.cancelled = true;
  }
  if (jobState.child) {
    try { jobState.child.kill('SIGKILL'); } catch { /* already gone */ }
  }
  urlfetch.killTree(urlJob.child);
  if (jobState.tempDir) {
    try { fs.rmSync(jobState.tempDir, { recursive: true, force: true }); } catch { /* best effort */ }
  }
});
