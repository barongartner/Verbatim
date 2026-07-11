'use strict';

const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { ModelManager } = require('./src/main/models');
const pipeline = require('./src/main/pipeline');

let win = null;
let modelManager = null;

const userDir = () => app.getPath('userData');
const settingsPath = () => path.join(userDir(), 'settings.json');
const transcriptsDir = () => path.join(userDir(), 'transcripts');

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

function projectFile(id) {
  if (!validProjectId(id)) throw new Error('Bad project id');
  return path.join(transcriptsDir(), `${id}.json`);
}

// ------------------------------------------------------------ transcription --
const jobState = { signal: null, child: null, tempDir: null };

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
    'webm', 'mp4', 'aiff', 'aif', 'caf', 'wma', 'amr', '3gp', 'json']);
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
    await fs.promises.rm(projectFile(id), { force: true });
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
    if (jobState.signal) {
      jobState.signal.cancelled = true;
      if (jobState.signal.abort) jobState.signal.abort();
    }
    if (jobState.child) {
      try { jobState.child.kill('SIGKILL'); } catch { /* already gone */ }
    }
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
  if (jobState.signal) jobState.signal.cancelled = true;
  if (jobState.child) {
    try { jobState.child.kill('SIGKILL'); } catch { /* already gone */ }
  }
  if (jobState.tempDir) {
    try { fs.rmSync(jobState.tempDir, { recursive: true, force: true }); } catch { /* best effort */ }
  }
});
