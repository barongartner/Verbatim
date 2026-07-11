'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');
const { spawn } = require('child_process');

// Everything is downloaded from the sherpa-onnx release pages on first use so
// installers stay small. All processing afterwards is 100% offline.
const GH = 'https://github.com/k2-fsa/sherpa-onnx/releases/download';

const WHISPER_MODELS = [
  {
    id: 'whisper-tiny',
    label: 'Whisper Tiny',
    detail: 'Fastest, good for quick drafts',
    downloadMB: 111,
    diskMB: 45,
    url: `${GH}/asr-models/sherpa-onnx-whisper-tiny.tar.bz2`,
    dir: 'sherpa-onnx-whisper-tiny',
    prefix: 'tiny'
  },
  {
    id: 'whisper-base',
    label: 'Whisper Base',
    detail: 'Recommended balance of speed and accuracy',
    downloadMB: 207,
    diskMB: 85,
    url: `${GH}/asr-models/sherpa-onnx-whisper-base.tar.bz2`,
    dir: 'sherpa-onnx-whisper-base',
    prefix: 'base'
  },
  {
    id: 'whisper-small',
    label: 'Whisper Small',
    detail: 'Most accurate, slower and a bigger download',
    downloadMB: 610,
    diskMB: 270,
    url: `${GH}/asr-models/sherpa-onnx-whisper-small.tar.bz2`,
    dir: 'sherpa-onnx-whisper-small',
    prefix: 'small'
  }
];

const SEGMENTATION = {
  id: 'segmentation',
  label: 'Speaker segmentation',
  downloadMB: 7,
  url: `${GH}/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2`,
  dir: 'sherpa-onnx-pyannote-segmentation-3-0',
  file: 'model.onnx'
};

const EMBEDDING = {
  id: 'embedding',
  label: 'Speaker voiceprints',
  downloadMB: 28,
  // NB: "recongition" typo is in the upstream release tag itself.
  url: `${GH}/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`,
  file: 'campplus_sv_zh_en_advanced.onnx'
};

class ModelManager {
  constructor(modelsDir) {
    this.modelsDir = modelsDir;
    fs.mkdirSync(modelsDir, { recursive: true });
  }

  whisperPaths(modelId) {
    const m = WHISPER_MODELS.find((w) => w.id === modelId);
    if (!m) throw new Error(`Unknown model: ${modelId}`);
    const dir = path.join(this.modelsDir, m.dir);
    return {
      encoder: path.join(dir, `${m.prefix}-encoder.int8.onnx`),
      decoder: path.join(dir, `${m.prefix}-decoder.int8.onnx`),
      tokens: path.join(dir, `${m.prefix}-tokens.txt`)
    };
  }

  segmentationPath() {
    return path.join(this.modelsDir, SEGMENTATION.dir, SEGMENTATION.file);
  }

  embeddingPath() {
    return path.join(this.modelsDir, EMBEDDING.file);
  }

  whisperReady(modelId) {
    const p = this.whisperPaths(modelId);
    return fs.existsSync(p.encoder) && fs.existsSync(p.decoder) && fs.existsSync(p.tokens);
  }

  diarizationReady() {
    return fs.existsSync(this.segmentationPath()) && fs.existsSync(this.embeddingPath());
  }

  listWhisper() {
    return WHISPER_MODELS.map((m) => ({
      id: m.id,
      label: m.label,
      detail: m.detail,
      downloadMB: m.downloadMB,
      diskMB: m.diskMB,
      downloaded: this.whisperReady(m.id)
    }));
  }

  // Downloads whatever is missing for a transcription run with modelId.
  // onProgress({item, label, received, total, status})
  async ensure(modelId, onProgress, signal) {
    const jobs = [];
    if (!this.whisperReady(modelId)) {
      const m = WHISPER_MODELS.find((w) => w.id === modelId);
      jobs.push({ kind: 'tar', item: m.id, label: m.label, url: m.url, dir: m.dir, after: () => this.pruneWhisper(m) });
    }
    if (!fs.existsSync(this.segmentationPath())) {
      jobs.push({ kind: 'tar', item: SEGMENTATION.id, label: SEGMENTATION.label, url: SEGMENTATION.url, dir: SEGMENTATION.dir });
    }
    if (!fs.existsSync(this.embeddingPath())) {
      jobs.push({ kind: 'file', item: EMBEDDING.id, label: EMBEDDING.label, url: EMBEDDING.url, dest: this.embeddingPath() });
    }
    for (const job of jobs) {
      if (signal && signal.cancelled) throw new Error('cancelled');
      const report = (received, total, status) =>
        onProgress && onProgress({ item: job.item, label: job.label, received, total, status });
      report(0, 0, 'downloading');
      if (job.kind === 'file') {
        await downloadWithRetry(job.url, job.dest, report, signal);
      } else {
        const tarPath = path.join(this.modelsDir, `${job.item}.tar.bz2`);
        await downloadWithRetry(job.url, tarPath, report, signal);
        if (signal && signal.cancelled) throw new Error('cancelled');
        report(0, 0, 'extracting');
        // Extract into a staging dir and move the finished directory into
        // place in one rename, so a crash or failed extraction can never
        // leave a half-written model that existsSync() mistakes for installed.
        const staging = path.join(this.modelsDir, `.staging-${job.item}`);
        try {
          await fs.promises.rm(staging, { recursive: true, force: true });
          await fs.promises.mkdir(staging, { recursive: true });
          await extractTarBz2(tarPath, staging);
          const src = path.join(staging, job.dir);
          if (!fs.existsSync(src)) throw new Error(`Archive did not contain ${job.dir}`);
          const dest = path.join(this.modelsDir, job.dir);
          await fs.promises.rm(dest, { recursive: true, force: true });
          await renameWithRetry(src, dest);
        } finally {
          fs.promises.rm(staging, { recursive: true, force: true }).catch(() => {});
          fs.promises.rm(tarPath, { force: true }).catch(() => {});
        }
        if (job.after) await job.after();
      }
      report(0, 0, 'done');
    }
    return jobs.length;
  }

  // The whisper archives ship fp32 + int8 copies of each network plus sample
  // wavs; we only use int8, so drop the rest (saves ~60% disk).
  async pruneWhisper(m) {
    const dir = path.join(this.modelsDir, m.dir);
    const junk = [
      path.join(dir, `${m.prefix}-encoder.onnx`),
      path.join(dir, `${m.prefix}-decoder.onnx`),
      path.join(dir, 'test_wavs')
    ];
    for (const j of junk) {
      await fs.promises.rm(j, { recursive: true, force: true }).catch(() => {});
    }
  }

  async storageBytes() {
    let total = 0;
    const walk = async (p) => {
      let st;
      try { st = await fs.promises.stat(p); } catch { return; }
      if (st.isDirectory()) {
        for (const f of await fs.promises.readdir(p)) await walk(path.join(p, f));
      } else {
        total += st.size;
      }
    };
    await walk(this.modelsDir);
    return total;
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function downloadWithRetry(url, dest, report, signal, attempts = 4) {
  let lastErr;
  for (let i = 0; i < attempts; i++) {
    if (signal && signal.cancelled) throw new Error('cancelled');
    try {
      return await downloadFile(url, dest, report, signal);
    } catch (e) {
      if ((signal && signal.cancelled) || /cancelled/i.test(String(e.message))) throw e;
      lastErr = e;
      await sleep(1500 * (i + 1)); // transient network errors: back off, resume from .part
    }
  }
  throw lastErr;
}

// Windows can transiently lock freshly-written files (Defender, indexer).
async function renameWithRetry(src, dest, attempts = 5) {
  for (let i = 0; ; i++) {
    try {
      return fs.renameSync(src, dest);
    } catch (e) {
      if (i >= attempts - 1 || !/EPERM|EBUSY|EACCES/i.test(String(e.code))) throw e;
      await sleep(250 * (i + 1));
    }
  }
}

// Single attempt. Resumes an existing .part via a Range request, verifies the
// byte count against Content-Length before moving into place, and settles on
// every failure path (response error included).
function downloadFile(url, dest, report, signal, redirects = 0) {
  return new Promise((resolve, reject) => {
    if (redirects > 6) return reject(new Error('Too many redirects'));
    const part = `${dest}.part`;
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    let start = 0;
    try { start = fs.statSync(part).size; } catch { start = 0; }
    const headers = { 'User-Agent': 'Verbatim' };
    if (start > 0) headers.Range = `bytes=${start}-`;

    let settled = false;
    const finish = (fn, arg) => { if (!settled) { settled = true; fn(arg); } };

    const req = https.get(url, { headers }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        res.resume();
        return finish(resolve, downloadFile(res.headers.location, dest, report, signal, redirects + 1));
      }
      if (res.statusCode !== 200 && res.statusCode !== 206) {
        res.resume();
        // A 416 means our .part is stale/oversized — drop it for next attempt.
        if (res.statusCode === 416) fs.rmSync(part, { force: true });
        return finish(reject, new Error(`Download failed (HTTP ${res.statusCode}) for ${url}`));
      }
      if (res.statusCode === 200) start = 0; // server ignored the Range: restart file
      const total = start + Number(res.headers['content-length'] || 0);
      let received = start;
      const out = fs.createWriteStream(part, { flags: res.statusCode === 206 ? 'a' : 'w' });
      res.on('data', (chunk) => {
        received += chunk.length;
        if (signal && signal.cancelled) {
          req.destroy(new Error('cancelled'));
          return;
        }
        report && report(received, total, 'downloading');
      });
      res.on('error', (e) => {
        out.destroy();
        finish(reject, signal && signal.cancelled ? new Error('cancelled') : e);
      });
      res.pipe(out);
      out.on('finish', () => {
        out.close(async () => {
          try {
            const size = fs.statSync(part).size;
            if (total > 0 && size !== total) {
              // Incomplete body with a clean end — keep nothing, it may be junk.
              fs.rmSync(part, { force: true });
              return finish(reject, new Error(`Incomplete download (${size} of ${total} bytes)`));
            }
            await renameWithRetry(part, dest);
            finish(resolve);
          } catch (e) {
            finish(reject, e);
          }
        });
      });
      out.on('error', (e) => finish(reject, e));
    });
    if (signal) signal.abort = () => req.destroy(new Error('cancelled'));
    req.on('error', (e) => {
      // keep the .part — the next attempt resumes it
      finish(reject, signal && signal.cancelled ? new Error('cancelled') : e);
    });
    req.setTimeout(60000, () => req.destroy(new Error('Download timed out')));
  });
}

// Both macOS (bsdtar) and Windows 10 1803+ (libarchive tar.exe in System32)
// can extract .tar.bz2 natively. On Windows, use the System32 binary by
// absolute path so a GNU tar earlier on PATH (MSYS2 etc.) can't shadow it.
function tarBinary() {
  if (process.platform === 'win32') {
    const sys = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe');
    if (fs.existsSync(sys)) return sys;
  }
  return 'tar';
}

function extractTarBz2(tarPath, destDir) {
  return new Promise((resolve, reject) => {
    const child = spawn(tarBinary(), ['-xjf', tarPath, '-C', destDir], { windowsHide: true });
    let err = '';
    child.stderr.on('data', (d) => { err += d; });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve();
      else reject(new Error(`tar exited with code ${code}: ${err.slice(0, 400)}`));
    });
  });
}

module.exports = { ModelManager, WHISPER_MODELS, SEGMENTATION, EMBEDDING };
