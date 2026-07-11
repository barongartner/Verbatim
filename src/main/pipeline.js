'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');
const { writeWavSlice } = require('./wav');

const SAMPLE_RATE = 16000;
const MAX_CHUNK_SEC = 28;   // whisper decodes a 30s window; keep headroom
const MERGE_GAP_SEC = 0.75; // join same-speaker segments separated by short pauses
const MIN_SEG_SEC = 0.15;
const ASR_BATCH = 8;        // wavs per sherpa-onnx-offline spawn (model loads once per spawn)

const exe = (binDir, name) =>
  path.join(binDir, process.platform === 'win32' ? `${name}.exe` : name);

function run(cmd, args, { onStderrLine, signal, track } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, { windowsHide: true });
    if (track) track(child);
    let out = '';
    let errBuf = '';
    let errTail = '';
    child.stdout.on('data', (d) => { out += d; });
    child.stderr.on('data', (d) => {
      errTail = (errTail + d.toString()).slice(-2000);
      if (!onStderrLine) return;
      errBuf += d.toString();
      let i;
      while ((i = errBuf.indexOf('\n')) >= 0) {
        onStderrLine(errBuf.slice(0, i));
        errBuf = errBuf.slice(i + 1);
      }
    });
    child.on('error', reject);
    child.on('close', (code) => {
      if (signal && signal.cancelled) return reject(new Error('cancelled'));
      if (code === 0) return resolve(out);
      reject(new Error(`${path.basename(cmd)} exited with code ${code}\n${errTail}`));
    });
  });
}

function parseDiarization(stdout) {
  const segs = [];
  for (const line of stdout.split('\n')) {
    const m = line.match(/^\s*(\d+(?:\.\d+)?)\s*--\s*(\d+(?:\.\d+)?)\s+(speaker[_ ]?\d+)/i);
    if (m) segs.push({ start: parseFloat(m[1]), end: parseFloat(m[2]), speaker: m[3] });
  }
  segs.sort((a, b) => a.start - b.start);
  return segs;
}

// Merge short same-speaker runs, then split anything longer than the whisper
// window into equal parts.
function shapeSegments(raw, totalSec) {
  let segs = raw.filter((s) => s.end - s.start >= MIN_SEG_SEC);
  if (!segs.length) {
    segs = [{ start: 0, end: totalSec, speaker: 'speaker_00' }];
  }
  const merged = [];
  for (const s of segs) {
    const last = merged[merged.length - 1];
    if (
      last &&
      last.speaker === s.speaker &&
      s.start - last.end <= MERGE_GAP_SEC &&
      s.end - last.start <= MAX_CHUNK_SEC
    ) {
      last.end = s.end;
    } else {
      merged.push({ ...s });
    }
  }
  const shaped = [];
  for (const s of merged) {
    const len = s.end - s.start;
    if (len <= MAX_CHUNK_SEC) {
      shaped.push(s);
      continue;
    }
    const parts = Math.ceil(len / MAX_CHUNK_SEC);
    const step = len / parts;
    for (let i = 0; i < parts; i++) {
      shaped.push({ start: s.start + i * step, end: s.start + (i + 1) * step, speaker: s.speaker });
    }
  }
  return shaped;
}

function parseAsrLines(stdout) {
  const results = [];
  for (const line of stdout.split('\n')) {
    const t = line.trim();
    if (!t.startsWith('{')) continue;
    try {
      const obj = JSON.parse(t);
      if (typeof obj.text === 'string') results.push({ text: obj.text.trim(), lang: obj.lang || '' });
    } catch {
      const m = t.match(/"text"\s*:\s*"((?:[^"\\]|\\.)*)"/);
      if (m) results.push({ text: JSON.parse(`"${m[1]}"`), lang: '' });
    }
  }
  return results;
}

function defaultThreads() {
  const n = os.cpus().length || 4;
  return Math.min(6, Math.max(2, n - 2));
}

/**
 * transcribe: full diarize-then-transcribe pipeline.
 * opts:
 *   pcm            Int16Array, 16 kHz mono
 *   binDir         directory containing the sherpa-onnx executables
 *   models         { segmentation, embedding, whisper: {encoder, decoder, tokens} }
 *   tempDir        scratch dir (created/removed by caller)
 *   language       '' for auto, or a whisper language code
 *   numSpeakers    0 for auto, or a fixed count
 *   onProgress     ({stage, pct, detail}) => void
 *   signal         { cancelled: bool }
 *   track          (child) => void, lets the caller kill the active process
 */
async function transcribe(opts) {
  const { pcm, binDir, models, tempDir, onProgress = () => {}, signal = {}, track } = opts;
  const totalSec = pcm.length / SAMPLE_RATE;
  const threads = String(opts.threads || defaultThreads());

  const checkCancel = () => {
    if (signal.cancelled) throw new Error('cancelled');
  };

  onProgress({ stage: 'prepare', pct: 0, detail: 'Preparing audio' });
  const masterWav = path.join(tempDir, 'audio.wav');
  await writeWavSlice(fs, masterWav, pcm, SAMPLE_RATE, 0, pcm.length);
  checkCancel();

  // --- Stage 1: who spoke when -------------------------------------------
  onProgress({ stage: 'diarize', pct: 0, detail: 'Identifying speakers' });
  const diarArgs = [
    `--segmentation.pyannote-model=${models.segmentation}`,
    `--embedding.model=${models.embedding}`,
    `--segmentation.num-threads=${threads}`,
    `--embedding.num-threads=${threads}`,
    Number(opts.numSpeakers) > 0
      ? `--clustering.num-clusters=${Number(opts.numSpeakers)}`
      : '--clustering.cluster-threshold=0.5',
    masterWav
  ];
  const diarOut = await run(exe(binDir, 'sherpa-onnx-offline-speaker-diarization'), diarArgs, {
    signal,
    track,
    onStderrLine: (line) => {
      const m = line.match(/progress\s+([\d.]+)%/i);
      if (m) onProgress({ stage: 'diarize', pct: parseFloat(m[1]), detail: 'Identifying speakers' });
    }
  });
  checkCancel();
  const segments = shapeSegments(parseDiarization(diarOut), totalSec);

  // --- Stage 2: what was said --------------------------------------------
  onProgress({ stage: 'transcribe', pct: 0, detail: 'Transcribing' });
  const chunkFiles = [];
  for (let i = 0; i < segments.length; i++) {
    const s = segments[i];
    const file = path.join(tempDir, `chunk_${String(i).padStart(4, '0')}.wav`);
    await writeWavSlice(
      fs, file, pcm, SAMPLE_RATE,
      Math.floor(s.start * SAMPLE_RATE),
      Math.ceil(s.end * SAMPLE_RATE)
    );
    chunkFiles.push(file);
  }
  checkCancel();

  const rows = [];
  const langCounts = {};
  for (let i = 0; i < segments.length; i += ASR_BATCH) {
    checkCancel();
    const batchSegs = segments.slice(i, i + ASR_BATCH);
    const batchFiles = chunkFiles.slice(i, i + ASR_BATCH);
    const asrArgs = [
      `--whisper-encoder=${models.whisper.encoder}`,
      `--whisper-decoder=${models.whisper.decoder}`,
      `--tokens=${models.whisper.tokens}`,
      `--num-threads=${threads}`
    ];
    if (opts.language) asrArgs.push(`--whisper-language=${opts.language}`);
    asrArgs.push(...batchFiles);

    const out = await run(exe(binDir, 'sherpa-onnx-offline'), asrArgs, { signal, track });
    const results = parseAsrLines(out);
    if (results.length !== batchSegs.length) {
      throw new Error(
        `Transcriber returned ${results.length} results for ${batchSegs.length} clips`
      );
    }
    for (let j = 0; j < batchSegs.length; j++) {
      const text = results[j].text;
      if (!text || /^[\s.\-,]*$/.test(text)) continue;
      // whisper's non-speech markers ("[Music]", "(applause)", "♪♪") aren't dialogue
      const trimmed = text.trim();
      const marker = /^[[(][^\])]{0,40}[\])]$/.test(trimmed) &&
        /music|applause|noise|silence|laughter|inaudible/i.test(trimmed);
      if (marker || /^[♪♩♫♬\s]+$/.test(trimmed)) continue;
      rows.push({
        start: round2(batchSegs[j].start),
        end: round2(batchSegs[j].end),
        speaker: batchSegs[j].speaker,
        text
      });
      if (results[j].lang) langCounts[results[j].lang] = (langCounts[results[j].lang] || 0) + 1;
    }
    onProgress({
      stage: 'transcribe',
      pct: Math.min(100, ((i + batchSegs.length) / segments.length) * 100),
      detail: `Transcribing ${Math.min(i + batchSegs.length, segments.length)} of ${segments.length} sections`
    });
  }

  const language =
    Object.entries(langCounts).sort((a, b) => b[1] - a[1]).map(([k]) => k)[0] || opts.language || '';
  onProgress({ stage: 'done', pct: 100, detail: 'Done' });
  return { segments: rows, language, durationSec: round2(totalSec) };
}

const round2 = (x) => Math.round(x * 100) / 100;

module.exports = { transcribe, shapeSegments, parseDiarization, parseAsrLines, defaultThreads, SAMPLE_RATE };
