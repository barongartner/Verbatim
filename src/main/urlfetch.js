'use strict';

// Fetch audio from a URL (YouTube, podcasts, direct media links, and the
// thousands of sites yt-dlp understands). The yt-dlp binary is downloaded
// into userData/tools on first use — keeping installers small and letting
// users update it from Settings when sites change their players.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');
const { downloadWithRetry } = require('./models');

const WIN = process.platform === 'win32';
const TOOL_URL = `https://github.com/yt-dlp/yt-dlp/releases/latest/download/${WIN ? 'yt-dlp.exe' : 'yt-dlp_macos'}`;
const MAX_DURATION_SEC = 4 * 3600;
const MAX_FILESIZE = '1400m'; // stays under the renderer's 1.5e9-byte ceiling

const toolPath = (toolsDir) => path.join(toolsDir, WIN ? 'yt-dlp.exe' : 'yt-dlp');

// yt-dlp's onefile binaries re-exec themselves: the PID we spawn is only a
// bootloader, so a plain kill leaves the real downloader running. Kill the
// whole tree — taskkill /T on Windows, process-group kill on POSIX (children
// are spawned detached so they lead their own group).
function killTree(child) {
  if (!child || child.pid == null) return;
  if (WIN) {
    try { spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true }); } catch { /* gone */ }
  } else {
    try { process.kill(-child.pid, 'SIGKILL'); } catch {
      try { child.kill('SIGKILL'); } catch { /* gone */ }
    }
  }
}

function runTool(cmd, args, { onStdoutLine, signal, track } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, { windowsHide: true, detached: !WIN });
    if (track) track(child);
    let out = '';
    let buf = '';
    let errTail = '';
    child.stdout.on('data', (d) => {
      out += d;
      if (!onStdoutLine) return;
      buf += d.toString();
      let i;
      // yt-dlp progress uses \r without --newline; handle both to be safe
      while ((i = buf.search(/[\r\n]/)) >= 0) {
        onStdoutLine(buf.slice(0, i));
        buf = buf.slice(i + 1);
      }
    });
    child.stderr.on('data', (d) => { errTail = (errTail + d.toString()).slice(-1500); });
    child.on('error', reject);
    child.on('close', (code) => {
      if (signal && signal.cancelled) return reject(new Error('cancelled'));
      if (code === 0) return resolve(out);
      // Surface yt-dlp's own ERROR line — it is far friendlier than an exit code.
      const errLine = (errTail.match(/ERROR:\s*(.+)/) || [])[1];
      reject(new Error(errLine ? errLine.slice(0, 300) : `yt-dlp exited with code ${code}\n${errTail}`));
    });
  });
}

async function ensureTool(toolsDir, onProgress, signal) {
  const tool = toolPath(toolsDir);
  if (fs.existsSync(tool)) return tool;
  fs.mkdirSync(toolsDir, { recursive: true });
  await downloadWithRetry(TOOL_URL, tool, (received, total) =>
    onProgress && onProgress({ stage: 'tool', pct: total ? (received / total) * 100 : 0, detail: 'Getting the link downloader (one time)' }),
  signal);
  if (!WIN) fs.chmodSync(tool, 0o755);
  return tool;
}

async function toolVersion(toolsDir) {
  const tool = toolPath(toolsDir);
  if (!fs.existsSync(tool)) return null;
  try {
    return (await runTool(tool, ['--version'])).trim();
  } catch {
    return null;
  }
}

async function updateTool(toolsDir, onProgress, signal, track) {
  const tool = await ensureTool(toolsDir, onProgress, signal);
  const out = await runTool(tool, ['-U'], { signal, track });
  const lines = out.trim().split('\n');
  return lines[lines.length - 1] || 'Updated';
}

async function fetchUrl({ url, toolsDir, mediaDir, onProgress = () => {}, signal = {}, track }) {
  url = String(url || '').trim();
  if (!/^https?:\/\/\S+$/i.test(url)) throw new Error('That does not look like a valid link');

  const tool = await ensureTool(toolsDir, onProgress, signal);
  if (signal.cancelled) throw new Error('cancelled');

  // Cheap metadata pass first, so we can refuse live streams and over-long
  // audio before downloading anything.
  onProgress({ stage: 'probe', pct: 0, detail: 'Reading the link' });
  // --no-playlist only helps for video-in-playlist URLs; a pure playlist URL
  // would still expand to every entry, so also hard-limit to the first item.
  // %(title)j prints the title JSON-encoded — guaranteed single-line, so the
  // line-based parse below can't be broken by titles containing newlines.
  const probeOut = await runTool(tool, [
    '--no-playlist', '--playlist-items', '1', '--skip-download',
    '--print', '%(title)j', '--print', '%(duration)s', '--print', '%(is_live)s',
    '--', url
  ], { signal, track });
  const lines = probeOut.trim().split('\n');
  const [titleRaw, durationRaw, isLive] = lines.slice(-3);
  let title = '';
  try { title = String(JSON.parse(titleRaw)); } catch { title = (titleRaw || '').trim(); }
  if (/^True$/i.test(isLive || '')) throw new Error('Live streams cannot be transcribed');
  const duration = parseFloat(durationRaw);
  if (Number.isFinite(duration) && duration > MAX_DURATION_SEC) {
    throw new Error('Audio longer than 4 hours is not supported yet');
  }
  if (signal.cancelled) throw new Error('cancelled');

  fs.mkdirSync(mediaDir, { recursive: true });
  const base = crypto.randomBytes(8).toString('hex');
  const discardPartials = () => {
    for (const f of fs.readdirSync(mediaDir)) {
      if (f.startsWith(`${base}.`)) fs.rmSync(path.join(mediaDir, f), { force: true });
    }
  };
  try {
    await runTool(tool, [
      '--no-playlist', '--playlist-items', '1', '--newline',
      '-f', 'bestaudio[ext=m4a]/bestaudio[ext=webm]/bestaudio/best',
      '--max-filesize', MAX_FILESIZE,
      '-o', path.join(mediaDir, `${base}.%(ext)s`),
      '--', url
    ], {
      signal,
      track,
      onStdoutLine: (line) => {
        const m = line.match(/\[download\]\s+([\d.]+)%/);
        if (m) onProgress({ stage: 'download', pct: parseFloat(m[1]), detail: title || 'Fetching audio' });
      }
    });
  } catch (e) {
    // cancelled or failed downloads must not strand .part/.ytdl files
    try { discardPartials(); } catch { /* best effort */ }
    throw e;
  }

  const file = fs.readdirSync(mediaDir).find((f) => f.startsWith(`${base}.`) && !f.endsWith('.part') && !f.endsWith('.ytdl'));
  if (!file) {
    try { discardPartials(); } catch { /* best effort */ }
    throw new Error('The audio was too large or could not be downloaded');
  }
  return {
    filePath: path.join(mediaDir, file),
    title: (title || 'Transcript').trim(),
    ext: path.extname(file).slice(1)
  };
}

module.exports = { fetchUrl, ensureTool, toolVersion, updateTool, toolPath, killTree };
