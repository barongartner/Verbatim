'use strict';

/* Verbatim renderer. No frameworks, no network — everything local. */

const $ = (id) => document.getElementById(id);

const els = {
  viewHome: $('view-home'), viewProgress: $('view-progress'), viewTranscript: $('view-transcript'),
  dropzone: $('dropzone'), fileInput: $('file-input'), btnOpen: $('btn-open'),
  btnOpenProject: $('btn-open-project'), btnSettings: $('btn-settings'),
  librarySection: $('library-section'), libraryList: $('library-list'),
  progFile: $('prog-file'), progStage: $('prog-stage'), progFill: $('prog-fill'),
  progDetail: $('prog-detail'), btnCancel: $('btn-cancel'),
  modelDl: $('model-dl'), modelDlItems: $('model-dl-items'), jobProgress: $('job-progress'),
  btnBack: $('btn-back'), tTitle: $('t-title'),
  search: $('search'), searchCount: $('search-count'), searchPrev: $('search-prev'), searchNext: $('search-next'),
  btnCopy: $('btn-copy'), btnExport: $('btn-export'), exportMenu: $('export-menu'),
  speakerBar: $('speaker-bar'), scroll: $('transcript-scroll'), list: $('transcript-list'),
  btnPlay: $('btn-play'), timeNow: $('time-now'), timeTotal: $('time-total'),
  waveform: $('waveform'), speed: $('speed'), btnFollow: $('btn-follow'),
  audioMissing: $('audio-missing'), btnRelink: $('btn-relink'),
  settingsModal: $('settings-modal'), modelList: $('model-list'),
  selLanguage: $('sel-language'), selSpeakers: $('sel-speakers'),
  storageInfo: $('storage-info'), btnModelsFolder: $('btn-models-folder'),
  aboutLine: $('about-line'), btnSettingsClose: $('btn-settings-close'),
  toast: $('toast'), audio: $('audio')
};

const SPEAKER_COLORS = ['#4cc2ff', '#ff9d6c', '#7ee2a8', '#e39bff', '#ffd54d',
  '#6c9fff', '#ff7fa5', '#5ee0d3', '#c9b458', '#9fb0c4'];

const LANGUAGES = [
  ['', 'Auto-detect'], ['en', 'English'], ['es', 'Spanish'], ['fr', 'French'], ['de', 'German'],
  ['it', 'Italian'], ['pt', 'Portuguese'], ['nl', 'Dutch'], ['sv', 'Swedish'], ['no', 'Norwegian'],
  ['da', 'Danish'], ['fi', 'Finnish'], ['pl', 'Polish'], ['cs', 'Czech'], ['uk', 'Ukrainian'],
  ['ru', 'Russian'], ['tr', 'Turkish'], ['ar', 'Arabic'], ['he', 'Hebrew'], ['hi', 'Hindi'],
  ['ja', 'Japanese'], ['ko', 'Korean'], ['zh', 'Chinese'], ['vi', 'Vietnamese'], ['th', 'Thai'],
  ['id', 'Indonesian'], ['el', 'Greek'], ['ro', 'Romanian'], ['hu', 'Hungarian'], ['ta', 'Tamil']
];

const state = {
  settings: { model: 'whisper-base', language: '', numSpeakers: 0 },
  models: [], library: [], platform: 'darwin', version: '',
  modelsDir: '', modelStorageBytes: 0,
  project: null,
  audioAvailable: false,
  transcribing: false,
  prepareCancelled: false,
  follow: true,
  search: { query: '', matches: [], current: -1 },
  saveTimer: null
};

/* ------------------------------------------------------------ utilities -- */

function fmtTime(t) {
  t = Math.max(0, Math.floor(t));
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
           : `${m}:${String(s).padStart(2, '0')}`;
}

function srtTime(t, sep) {
  const ms = Math.round((t % 1) * 1000);
  t = Math.floor(t);
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}${sep}${String(ms).padStart(3, '0')}`;
}

function escapeHtml(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Case-insensitive literal search returning [{i, len}] against the ORIGINAL
// string (indexOf over toLowerCase() breaks when lowercasing changes length).
function findMatches(text, query) {
  const out = [];
  if (!query) return out;
  const re = new RegExp(escapeRegex(query), 'gi');
  let m;
  while ((m = re.exec(text)) !== null) {
    out.push({ i: m.index, len: m[0].length });
    if (re.lastIndex === m.index) re.lastIndex++;
  }
  return out;
}

function fmtBytes(n) {
  if (n > 1e9) return `${(n / 1e9).toFixed(2)} GB`;
  if (n > 1e6) return `${Math.round(n / 1e6)} MB`;
  return `${Math.round(n / 1e3)} KB`;
}

function fmtDate(iso) {
  try {
    return new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });
  } catch { return ''; }
}

let toastTimer = null;
function toast(msg, ms = 2600) {
  els.toast.onclick = null; // a previous toast may have armed a click action
  els.toast.textContent = msg;
  els.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { els.toast.hidden = true; }, ms);
}

function showView(name) {
  els.viewHome.hidden = name !== 'home';
  els.viewProgress.hidden = name !== 'progress';
  els.viewTranscript.hidden = name !== 'transcript';
}

let currentAudioURL = null;
function setAudioSource(blobOrFile) {
  if (currentAudioURL) URL.revokeObjectURL(currentAudioURL);
  currentAudioURL = blobOrFile ? URL.createObjectURL(blobOrFile) : null;
  if (currentAudioURL) {
    els.audio.src = currentAudioURL;
  } else {
    els.audio.removeAttribute('src');
    els.audio.load();
  }
}

function speakerName(key) {
  const sp = state.project?.speakers?.[key];
  return sp ? sp.name : key;
}
function speakerColor(key) {
  const sp = state.project?.speakers?.[key];
  return sp ? sp.color : '#8b95a5';
}

/* --------------------------------------------------------------- decode -- */

async function decodeAudio(arrayBuffer) {
  const probe = new AudioContext();
  let buf;
  try {
    buf = await probe.decodeAudioData(arrayBuffer);
  } finally {
    probe.close();
  }
  const frames = Math.ceil(buf.duration * 16000);
  const off = new OfflineAudioContext(1, frames, 16000);
  const src = off.createBufferSource();
  src.buffer = buf;
  src.connect(off.destination);
  src.start();
  const rendered = await off.startRendering();
  const f32 = rendered.getChannelData(0);
  const i16 = new Int16Array(f32.length);
  for (let i = 0; i < f32.length; i++) {
    const v = Math.max(-1, Math.min(1, f32[i]));
    i16[i] = v < 0 ? v * 32768 : v * 32767;
  }
  return { i16, duration: buf.duration, peaks: computePeaks(f32) };
}

function computePeaks(f32, buckets = 1500) {
  const n = Math.min(buckets, f32.length || 1);
  const per = Math.max(1, Math.floor(f32.length / n));
  const peaks = new Array(n).fill(0);
  for (let b = 0; b < n; b++) {
    let max = 0;
    const end = Math.min(f32.length, (b + 1) * per);
    for (let i = b * per; i < end; i += 4) {
      const a = Math.abs(f32[i]);
      if (a > max) max = a;
    }
    peaks[b] = Math.round(max * 1000) / 1000;
  }
  return peaks;
}

/* ------------------------------------------------------------ transcribe -- */

function transcribeFile(file) {
  return transcribeBlob(file, file.name, window.verbatim.pathForFile(file) || '');
}

// Reads duration from container metadata without decoding the whole file, so
// over-long files are rejected before the memory-hungry decode.
function probeDuration(file) {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const probe = new Audio();
    let settled = false;
    const done = (v) => {
      if (settled) return;
      settled = true;
      URL.revokeObjectURL(url);
      resolve(v);
    };
    probe.preload = 'metadata';
    probe.onloadedmetadata = () => done(probe.duration);
    probe.onerror = () => done(NaN);
    setTimeout(() => done(NaN), 10000);
    probe.src = url;
  });
}

const cancelledDuringPrepare = () => {
  if (state.prepareCancelled) throw new Error('cancelled');
};

async function transcribeBlob(file, fileName, filePath) {
  if (state.transcribing) return;
  state.transcribing = true;
  state.prepareCancelled = false;
  els.progFile.textContent = fileName;
  els.modelDl.hidden = true;
  els.modelDlItems.innerHTML = '';
  setProgress('Preparing audio…', 2, '');
  showView('progress');
  try {
    if (file.size > 1.5e9) throw new Error('Files over 1.5 GB are not supported yet');
    const probed = await probeDuration(file);
    if (Number.isFinite(probed) && probed > 4 * 3600) {
      throw new Error('Files longer than 4 hours are not supported yet');
    }
    cancelledDuringPrepare();
    const bytes = await file.arrayBuffer();
    const { i16, duration, peaks } = await decodeAudio(bytes);
    if (duration > 4 * 3600) throw new Error('Files longer than 4 hours are not supported yet');
    cancelledDuringPrepare();
    setProgress('Starting up…', 4, '');

    const result = await window.verbatim.transcribe(i16.buffer, {
      model: state.settings.model,
      language: state.settings.language,
      numSpeakers: state.settings.numSpeakers
    });

    if (!result.segments.length) {
      toast('No speech was found in that audio', 5000);
      showView('home');
      refreshHome();
      return;
    }

    const speakers = {};
    const keys = [...new Set(result.segments.map((s) => s.speaker))];
    keys.sort();
    keys.forEach((k, i) => {
      speakers[k] = { name: `Speaker ${i + 1}`, color: SPEAKER_COLORS[i % SPEAKER_COLORS.length] };
    });

    state.project = {
      id: null,
      app: 'verbatim',
      appVersion: state.version,
      title: fileName.replace(/\.[^.]+$/, ''),
      audioName: fileName,
      audioPath: filePath,
      durationSec: result.durationSec || duration,
      language: result.language,
      model: state.settings.model,
      speakers,
      segments: result.segments,
      peaks
    };
    state.audioAvailable = true;
    setAudioSource(file);
    await persistProject();
    openTranscriptView();
    toast(keys.length === 1 ? 'Transcribed — 1 speaker detected' : `Transcribed — ${keys.length} speakers detected`);
  } catch (err) {
    const msg = String(err && err.message || err);
    if (/cancelled/i.test(msg)) toast('Transcription cancelled');
    else { console.error(err); toast(`Something went wrong: ${msg.split('\n')[0].replace(/^.*Error: /, '')}`, 6000); }
    showView('home');
    refreshHome();
  } finally {
    state.transcribing = false;
  }
}

function setProgress(stage, pct, detail) {
  els.progStage.textContent = stage;
  els.progFill.style.width = `${Math.max(0, Math.min(100, pct))}%`;
  els.progDetail.textContent = detail || '';
}

window.verbatim.onTranscribeProgress((p) => {
  if (!state.transcribing) return;
  els.modelDl.hidden = true;
  if (p.stage === 'prepare') setProgress('Preparing audio…', 4, '');
  else if (p.stage === 'diarize') setProgress('Listening for speakers…', 5 + p.pct * 0.4, `${Math.round(p.pct)}%`);
  else if (p.stage === 'transcribe') setProgress('Writing the transcript…', 45 + p.pct * 0.55, p.detail);
  else if (p.stage === 'done') setProgress('Finishing…', 100, '');
});

window.verbatim.onModelProgress((p) => {
  if (!state.transcribing) return;
  els.modelDl.hidden = false;
  let row = document.getElementById(`dl-${p.item}`);
  if (!row) {
    row = document.createElement('div');
    row.className = 'dl-item';
    row.id = `dl-${p.item}`;
    row.innerHTML = `<div class="dl-label"><span></span><span class="dl-size dim"></span></div>
                     <div class="bar"><div class="bar-fill"></div></div>`;
    els.modelDlItems.appendChild(row);
  }
  row.querySelector('.dl-label span').textContent = p.label;
  const sizeEl = row.querySelector('.dl-size');
  const fill = row.querySelector('.bar-fill');
  if (p.status === 'downloading' && p.total) {
    fill.style.width = `${(p.received / p.total) * 100}%`;
    sizeEl.textContent = `${fmtBytes(p.received)} / ${fmtBytes(p.total)}`;
  } else if (p.status === 'extracting') {
    fill.style.width = '100%';
    sizeEl.textContent = 'Unpacking…';
  } else if (p.status === 'done') {
    fill.style.width = '100%';
    sizeEl.textContent = 'Ready';
  }
});

els.btnCancel.addEventListener('click', () => {
  state.prepareCancelled = true; // covers the local decode stage
  window.verbatim.cancelTranscribe(); // covers downloads + the pipeline
});

/* ------------------------------------------------------------- project -- */

async function persistProject() {
  if (!state.project) return;
  const meta = await window.verbatim.saveProject(state.project);
  state.project.id = meta.id;
  state.project.createdAt = meta.createdAt;
  state.project.updatedAt = meta.updatedAt;
}

function scheduleSave() {
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(() => persistProject().catch(() => {}), 800);
}

async function openProjectById(id) {
  try {
    const project = await window.verbatim.loadProject(id);
    await openProject(project);
  } catch (e) {
    toast('Could not open that transcript');
  }
}

// Accepts project JSON from disk (possibly hand-edited or from another
// machine) — normalize before trusting it.
function normalizeProject(p) {
  if (!p || typeof p !== 'object' || !Array.isArray(p.segments)) return null;
  const segments = p.segments
    .filter((s) => s && typeof s.text === 'string')
    .map((s) => ({
      start: Number(s.start) || 0,
      end: Number(s.end) || 0,
      speaker: String(s.speaker || 'speaker_00'),
      text: s.text
    }));
  const speakers = {};
  const keys = [...new Set(segments.map((s) => s.speaker))].sort();
  keys.forEach((k, i) => {
    const given = p.speakers && typeof p.speakers === 'object' ? p.speakers[k] : null;
    speakers[k] = {
      name: given && typeof given.name === 'string' ? given.name : `Speaker ${i + 1}`,
      color: given && /^#[0-9a-f]{3,8}$/i.test(given.color || '') ? given.color : SPEAKER_COLORS[i % SPEAKER_COLORS.length]
    };
  });
  return {
    id: typeof p.id === 'string' ? p.id : null,
    app: 'verbatim',
    appVersion: p.appVersion || '',
    title: typeof p.title === 'string' ? p.title : 'Untitled',
    audioName: typeof p.audioName === 'string' ? p.audioName : '',
    audioPath: typeof p.audioPath === 'string' ? p.audioPath : '',
    durationSec: Number(p.durationSec) || (segments.length ? segments[segments.length - 1].end : 0),
    language: typeof p.language === 'string' ? p.language : '',
    model: typeof p.model === 'string' ? p.model : '',
    createdAt: p.createdAt,
    updatedAt: p.updatedAt,
    speakers,
    segments,
    peaks: Array.isArray(p.peaks) ? p.peaks.map(Number).filter(Number.isFinite) : null
  };
}

async function openProject(rawProject) {
  const project = normalizeProject(rawProject);
  if (!project) { toast('That file is not a Verbatim transcript'); return; }
  state.project = project;
  state.audioAvailable = false;
  setAudioSource(null);
  if (project.audioPath) {
    try {
      const bytes = await window.verbatim.readFile(project.audioPath);
      setAudioSource(new Blob([bytes]));
      state.audioAvailable = true;
    } catch { /* audio moved or deleted */ }
  }
  openTranscriptView();
}

/* -------------------------------------------------------- transcript UI -- */

function openTranscriptView() {
  const p = state.project;
  els.tTitle.value = p.title || 'Untitled';
  els.search.value = '';
  state.search = { query: '', matches: [], current: -1 };
  updateSearchUi();
  renderSpeakerBar();
  renderTranscript();
  els.timeTotal.textContent = fmtTime(p.durationSec || 0);
  els.timeNow.textContent = '0:00';
  els.audioMissing.hidden = state.audioAvailable;
  els.btnPlay.disabled = !state.audioAvailable;
  showView('transcript');
  requestAnimationFrame(drawWaveform);
}

function renderSpeakerBar() {
  const p = state.project;
  els.speakerBar.innerHTML = '';
  const counts = {};
  for (const s of p.segments) counts[s.speaker] = (counts[s.speaker] || 0) + 1;
  for (const key of Object.keys(p.speakers)) {
    const chip = document.createElement('button');
    chip.className = 'speaker-chip';
    chip.title = 'Click to rename';
    chip.innerHTML = `<span class="dot"></span><span class="name"></span><span class="count"></span>`;
    chip.querySelector('.dot').style.background = p.speakers[key].color;
    chip.querySelector('.name').textContent = p.speakers[key].name;
    chip.querySelector('.count').textContent = counts[key] || 0;
    chip.addEventListener('click', () => beginRename(chip, key));
    els.speakerBar.appendChild(chip);
  }
  const hint = document.createElement('span');
  hint.className = 'speaker-hint';
  hint.textContent = 'Click a speaker to rename · double-click text to edit';
  els.speakerBar.appendChild(hint);
}

function beginRename(chip, key) {
  if (chip.querySelector('input')) return;
  const nameEl = chip.querySelector('.name');
  const input = document.createElement('input');
  input.value = state.project.speakers[key].name;
  nameEl.replaceWith(input);
  input.focus();
  input.select();
  const commit = () => {
    const v = input.value.trim();
    if (v) state.project.speakers[key].name = v;
    renderSpeakerBar();
    renderTranscript();
    scheduleSave();
  };
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') input.blur();
    if (e.key === 'Escape') { input.value = state.project.speakers[key].name; input.blur(); }
    e.stopPropagation();
  });
  input.addEventListener('blur', commit, { once: true });
  input.addEventListener('click', (e) => e.stopPropagation());
}

function segmentHtml(seg, query) {
  if (!query) return escapeHtml(seg.text);
  let html = '';
  let pos = 0;
  for (const { i, len } of findMatches(seg.text, query)) {
    html += escapeHtml(seg.text.slice(pos, i));
    html += `<mark>${escapeHtml(seg.text.slice(i, i + len))}</mark>`;
    pos = i + len;
  }
  html += escapeHtml(seg.text.slice(pos));
  return html;
}

function renderTranscript() {
  const p = state.project;
  const q = state.search.query;
  const frag = document.createDocumentFragment();
  p.segments.forEach((seg, i) => {
    const row = document.createElement('div');
    row.className = 'seg';
    row.dataset.i = i;
    const time = document.createElement('div');
    time.className = 'seg-time';
    time.textContent = fmtTime(seg.start);
    time.title = 'Jump to this moment';
    const who = document.createElement('div');
    who.className = 'seg-speaker';
    who.textContent = speakerName(seg.speaker);
    who.style.color = speakerColor(seg.speaker);
    const text = document.createElement('div');
    text.className = 'seg-text';
    text.innerHTML = segmentHtml(seg, q);
    row.append(time, who, text);
    frag.appendChild(row);
  });
  els.list.innerHTML = '';
  els.list.appendChild(frag);
}

/* row interactions (delegated) */
els.list.addEventListener('click', (e) => {
  const row = e.target.closest('.seg');
  if (!row) return;
  if (e.target.closest('.seg-time')) {
    seekTo(state.project.segments[row.dataset.i].start, true);
  }
});
els.list.addEventListener('dblclick', (e) => {
  const textEl = e.target.closest('.seg-text');
  if (!textEl) return;
  const row = textEl.closest('.seg');
  const seg = state.project.segments[row.dataset.i];
  textEl.contentEditable = 'plaintext-only';
  textEl.textContent = seg.text; // drop <mark>s while editing
  textEl.focus();
  const done = () => {
    textEl.contentEditable = 'false';
    // collapse pasted newlines/whitespace — segments are single lines
    const v = textEl.textContent.replace(/\s+/g, ' ').trim();
    if (v && v !== seg.text) {
      seg.text = v;
      scheduleSave();
      runSearch(state.search.query, false);
    }
    textEl.innerHTML = segmentHtml(seg, state.search.query);
  };
  textEl.addEventListener('blur', done, { once: true });
  textEl.addEventListener('keydown', (e2) => {
    if (e2.key === 'Enter') { e2.preventDefault(); textEl.blur(); }
    if (e2.key === 'Escape') { textEl.textContent = seg.text; textEl.blur(); }
    e2.stopPropagation();
  });
});

/* --------------------------------------------------------------- player -- */

function seekTo(t, autoplay) {
  if (!state.audioAvailable) return;
  els.audio.currentTime = Math.max(0, t);
  if (autoplay && els.audio.paused) els.audio.play().catch(() => {});
}

els.btnPlay.addEventListener('click', () => {
  if (!state.audioAvailable) return;
  if (els.audio.paused) els.audio.play().catch(() => {});
  else els.audio.pause();
});
els.audio.addEventListener('play', () => { els.btnPlay.innerHTML = '&#10074;&#10074;'; });
els.audio.addEventListener('pause', () => { els.btnPlay.innerHTML = '&#9654;'; });
// the media load algorithm resets playbackRate to 1 on every new src
els.audio.addEventListener('loadedmetadata', () => {
  els.audio.playbackRate = parseFloat(els.speed.value);
});

let activeRow = null;
els.audio.addEventListener('timeupdate', () => {
  els.timeNow.textContent = fmtTime(els.audio.currentTime);
  drawWaveform();
  const segs = state.project?.segments;
  if (!segs) return;
  const t = els.audio.currentTime;
  let idx = -1;
  for (let i = 0; i < segs.length; i++) {
    if (t >= segs[i].start - 0.05 && t < segs[i].end + 0.2) { idx = i; break; }
    if (segs[i].start > t) break;
  }
  const row = idx >= 0 ? els.list.querySelector(`.seg[data-i="${idx}"]`) : null;
  if (row !== activeRow) {
    activeRow?.classList.remove('active');
    activeRow = row;
    if (row) {
      row.classList.add('active');
      if (state.follow && !els.viewTranscript.hidden) {
        row.scrollIntoView({ block: 'center', behavior: 'smooth' });
      }
    }
  }
});

els.speed.addEventListener('change', () => { els.audio.playbackRate = parseFloat(els.speed.value); });
els.btnFollow.addEventListener('click', () => {
  state.follow = !state.follow;
  els.btnFollow.classList.toggle('on', state.follow);
});

function drawWaveform() {
  const canvas = els.waveform;
  const peaks = state.project?.peaks;
  const rect = canvas.getBoundingClientRect();
  if (rect.width === 0) return;
  const dpr = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(rect.width * dpr)) {
    canvas.width = Math.round(rect.width * dpr);
    canvas.height = Math.round(rect.height * dpr);
  }
  const ctx = canvas.getContext('2d');
  const W = canvas.width, H = canvas.height;
  ctx.clearRect(0, 0, W, H);
  if (!peaks || !peaks.length) return;
  const dur = state.project.durationSec || 1;
  const playedX = (els.audio.currentTime / dur) * W;
  const n = peaks.length;
  const bw = W / n;
  for (let i = 0; i < n; i++) {
    const h = Math.max(2 * dpr, peaks[i] * (H - 6 * dpr));
    const x = i * bw;
    ctx.fillStyle = x <= playedX ? '#4cc2ff' : '#2a3442';
    ctx.fillRect(x, (H - h) / 2, Math.max(1, bw - 1 * dpr), h);
  }
}

function waveformSeek(e) {
  const rect = els.waveform.getBoundingClientRect();
  const pct = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
  seekTo(pct * (state.project?.durationSec || 0), false);
  drawWaveform();
}
els.waveform.addEventListener('mousedown', (e) => {
  waveformSeek(e);
  const move = (e2) => waveformSeek(e2);
  const up = () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up); };
  window.addEventListener('mousemove', move);
  window.addEventListener('mouseup', up);
});
window.addEventListener('resize', () => { if (!els.viewTranscript.hidden) drawWaveform(); });

/* --------------------------------------------------------------- search -- */

function runSearch(query, jump = true) {
  state.search.query = query;
  const matches = [];
  if (query) {
    state.project.segments.forEach((seg, si) => {
      for (const _m of findMatches(seg.text, query)) matches.push({ seg: si });
    });
  }
  state.search.matches = matches;
  state.search.current = matches.length ? 0 : -1;
  renderTranscript();
  updateSearchUi();
  if (jump && matches.length) focusMatch(0, false);
}

function updateSearchUi() {
  const { matches, current, query } = state.search;
  const has = !!query;
  els.searchCount.hidden = !has;
  els.searchPrev.hidden = !has;
  els.searchNext.hidden = !has;
  els.searchCount.textContent = matches.length ? `${current + 1}/${matches.length}` : '0/0';
}

function focusMatch(index, seek = true) {
  const { matches } = state.search;
  if (!matches.length) return;
  state.search.current = ((index % matches.length) + matches.length) % matches.length;
  updateSearchUi();
  const m = matches[state.search.current];
  document.querySelectorAll('.seg-text mark.current').forEach((el) => el.classList.remove('current'));
  const row = els.list.querySelector(`.seg[data-i="${m.seg}"]`);
  if (!row) return;
  // nth mark within this row
  let nth = 0;
  for (let i = 0; i < state.search.current; i++) if (matches[i].seg === m.seg) nth++;
  const mark = row.querySelectorAll('mark')[nth];
  (mark || row).classList.add('current');
  row.scrollIntoView({ block: 'center' });
  if (seek) seekTo(state.project.segments[m.seg].start, false);
}

let searchTimer = null;
els.search.addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => runSearch(els.search.value.trim()), 180);
});
els.search.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') focusMatch(state.search.current + (e.shiftKey ? -1 : 1));
  if (e.key === 'Escape') { els.search.value = ''; runSearch(''); els.search.blur(); }
  e.stopPropagation();
});
els.searchNext.addEventListener('click', () => focusMatch(state.search.current + 1));
els.searchPrev.addEventListener('click', () => focusMatch(state.search.current - 1));

/* -------------------------------------------------------------- exports -- */

function buildTxt() {
  const p = state.project;
  const lines = [p.title, `Transcribed by Verbatim — ${fmtDate(p.createdAt || new Date().toISOString())}`, ''];
  for (const s of p.segments) {
    lines.push(`[${fmtTime(s.start)}] ${speakerName(s.speaker)}: ${s.text}`);
  }
  return lines.join('\n') + '\n';
}

const oneLine = (s) => s.replace(/\s+/g, ' ').trim();

function buildSrt() {
  return state.project.segments.map((s, i) =>
    `${i + 1}\n${srtTime(s.start, ',')} --> ${srtTime(s.end, ',')}\n${oneLine(speakerName(s.speaker))}: ${oneLine(s.text)}\n`
  ).join('\n');
}

function buildVtt() {
  const esc = (s) => oneLine(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  return 'WEBVTT\n\n' + state.project.segments.map((s) =>
    `${srtTime(s.start, '.')} --> ${srtTime(s.end, '.')}\n<v ${esc(speakerName(s.speaker))}>${esc(s.text)}\n`
  ).join('\n');
}

async function doExport(fmt) {
  els.exportMenu.hidden = true;
  const p = state.project;
  let base = (p.title || 'transcript').replace(/[\\/:*?"<>|]/g, '_').trim() || 'transcript';
  if (/^(CON|PRN|AUX|NUL|COM\d|LPT\d)$/i.test(base.split('.')[0])) base = `_${base}`;
  try {
    if (fmt === 'project') {
      const saved = await window.verbatim.exportProjectFile(p);
      if (saved) toast(`Project saved to ${saved}`);
      return;
    }
    const eol = (s) => (state.platform === 'win32' ? s.replace(/\n/g, '\r\n') : s);
    const map = {
      txt: { content: eol(buildTxt()), name: `${base}.txt`, filter: 'Text' },
      srt: { content: eol(buildSrt()), name: `${base}.srt`, filter: 'SubRip subtitles' },
      vtt: { content: buildVtt(), name: `${base}.vtt`, filter: 'WebVTT subtitles' },
      json: { content: JSON.stringify(p, null, 1), name: `${base}.json`, filter: 'JSON' }
    };
    const job = map[fmt];
    const saved = await window.verbatim.saveTextFile({
      defaultName: job.name, content: job.content, filterName: job.filter, filterExt: fmt
    });
    if (saved) {
      toast('Exported ✓  (click to reveal)', 4000);
      els.toast.onclick = () => { window.verbatim.showInFolder(saved); els.toast.hidden = true; };
    }
  } catch (e) {
    toast('Export failed');
  }
}

els.btnExport.addEventListener('click', (e) => {
  e.stopPropagation();
  els.exportMenu.hidden = !els.exportMenu.hidden;
});
document.addEventListener('click', () => { els.exportMenu.hidden = true; });
els.exportMenu.addEventListener('click', (e) => {
  const b = e.target.closest('button[data-fmt]');
  if (b) doExport(b.dataset.fmt);
  e.stopPropagation();
});

els.btnCopy.addEventListener('click', async () => {
  await navigator.clipboard.writeText(buildTxt());
  toast('Transcript copied to clipboard');
});

/* --------------------------------------------------------- header/misc -- */

els.tTitle.addEventListener('change', () => {
  if (!state.project) return;
  state.project.title = els.tTitle.value.trim() || 'Untitled';
  scheduleSave();
});

els.btnBack.addEventListener('click', async () => {
  els.audio.pause();
  clearTimeout(state.saveTimer);
  if (state.project) await persistProject().catch(() => {});
  showView('home');
  refreshHome();
});

els.btnRelink.addEventListener('click', () => {
  els.fileInput.dataset.mode = 'relink';
  els.fileInput.click();
});

/* ----------------------------------------------------------------- home -- */

async function refreshHome() {
  const st = await window.verbatim.getState();
  state.settings = st.settings;
  state.models = st.models;
  state.library = st.library;
  state.platform = st.platform;
  state.version = st.version;
  state.modelsDir = st.modelsDir;
  state.modelStorageBytes = st.modelStorageBytes;
  document.body.className = `platform-${st.platform === 'darwin' ? 'mac' : st.platform === 'win32' ? 'win' : 'linux'}`;
  renderLibrary();
}

function renderLibrary() {
  els.librarySection.hidden = !state.library.length;
  els.libraryList.innerHTML = '';
  for (const entry of state.library) {
    const card = document.createElement('div');
    card.className = 'lib-card';
    const main = document.createElement('div');
    main.className = 'lib-main';
    const names = (entry.speakerNames || []).slice(0, 4).join(', ');
    main.innerHTML = `<div class="lib-title"></div>
      <div class="lib-meta"></div>
      <div class="lib-snippet"></div>`;
    main.querySelector('.lib-title').textContent = entry.title || entry.audioName;
    main.querySelector('.lib-meta').textContent =
      `${fmtTime(entry.durationSec || 0)} · ${names} · ${fmtDate(entry.updatedAt)}`;
    main.querySelector('.lib-snippet').textContent = entry.snippet || '';
    const del = document.createElement('button');
    del.className = 'lib-del';
    del.innerHTML = '&#10005;';
    del.title = 'Delete transcript';
    del.addEventListener('click', async (e) => {
      e.stopPropagation();
      if (!confirm(`Delete transcript "${entry.title}"? The audio file is not touched.`)) return;
      state.library = await window.verbatim.deleteProject(entry.id);
      renderLibrary();
    });
    card.append(main, del);
    card.addEventListener('click', () => openProjectById(entry.id));
    els.libraryList.appendChild(card);
  }
}

/* file choosing */
els.btnOpen.addEventListener('click', (e) => { e.stopPropagation(); els.fileInput.dataset.mode = 'new'; els.fileInput.click(); });
els.dropzone.addEventListener('click', () => { els.fileInput.dataset.mode = 'new'; els.fileInput.click(); });
els.fileInput.addEventListener('change', async () => {
  const file = els.fileInput.files[0];
  els.fileInput.value = '';
  if (!file) return;
  if (els.fileInput.dataset.mode === 'relink') {
    state.project.audioPath = window.verbatim.pathForFile(file) || state.project.audioPath;
    state.project.audioName = file.name;
    setAudioSource(file);
    state.audioAvailable = true;
    els.audioMissing.hidden = true;
    els.btnPlay.disabled = false;
    scheduleSave();
    toast('Audio linked');
  } else {
    transcribeFile(file);
  }
});

['dragover', 'dragenter'].forEach((ev) =>
  document.addEventListener(ev, (e) => {
    e.preventDefault();
    if (!els.viewHome.hidden) els.dropzone.classList.add('drag');
  })
);
['dragleave', 'drop'].forEach((ev) =>
  document.addEventListener(ev, (e) => {
    e.preventDefault();
    els.dropzone.classList.remove('drag');
  })
);
document.addEventListener('drop', (e) => {
  if (els.viewHome.hidden || state.transcribing) return;
  const file = e.dataTransfer.files && e.dataTransfer.files[0];
  if (file) transcribeFile(file);
});

els.btnOpenProject.addEventListener('click', async () => {
  try {
    const project = await window.verbatim.openProjectDialog();
    if (!project) return;
    // Imported copies get their own identity so they can never silently
    // overwrite an existing library entry that shares the embedded id.
    delete project.id;
    await openProject(project);
  } catch {
    toast('Could not read that file — it does not look like a Verbatim transcript');
  }
});

/* -------------------------------------------------------------- settings -- */

function renderSettings() {
  els.modelList.innerHTML = '';
  for (const m of state.models) {
    const row = document.createElement('div');
    row.className = 'model-row' + (state.settings.model === m.id ? ' selected' : '');
    row.innerHTML = `<div class="model-info">
        <div class="model-name"></div><div class="model-detail"></div>
      </div><span class="model-size"></span>`;
    row.querySelector('.model-name').textContent = m.label;
    row.querySelector('.model-detail').textContent = m.detail;
    const size = row.querySelector('.model-size');
    if (m.downloaded) {
      size.outerHTML = '<span class="model-badge">Downloaded</span>';
    } else {
      size.textContent = `${m.downloadMB} MB download`;
    }
    row.addEventListener('click', async () => {
      state.settings = await window.verbatim.setSettings({ model: m.id });
      renderSettings();
    });
    els.modelList.appendChild(row);
  }

  els.selLanguage.innerHTML = '';
  for (const [code, name] of LANGUAGES) {
    const o = document.createElement('option');
    o.value = code;
    o.textContent = name;
    els.selLanguage.appendChild(o);
  }
  els.selLanguage.value = state.settings.language || '';

  els.selSpeakers.innerHTML = '';
  const auto = document.createElement('option');
  auto.value = '0';
  auto.textContent = 'Detect automatically';
  els.selSpeakers.appendChild(auto);
  for (let i = 1; i <= 8; i++) {
    const o = document.createElement('option');
    o.value = String(i);
    o.textContent = i === 1 ? '1 (just me)' : String(i);
    els.selSpeakers.appendChild(o);
  }
  els.selSpeakers.value = String(state.settings.numSpeakers || 0);

  els.storageInfo.textContent = fmtBytes(state.modelStorageBytes || 0);
  els.aboutLine.textContent = `Verbatim ${state.version} — Whisper + pyannote, fully offline`;
}

els.btnSettings.addEventListener('click', async () => {
  await refreshHome();
  renderSettings();
  els.settingsModal.hidden = false;
});
els.btnSettingsClose.addEventListener('click', () => { els.settingsModal.hidden = true; });
els.settingsModal.addEventListener('click', (e) => {
  if (e.target === els.settingsModal) els.settingsModal.hidden = true;
});
els.selLanguage.addEventListener('change', async () => {
  state.settings = await window.verbatim.setSettings({ language: els.selLanguage.value });
});
els.selSpeakers.addEventListener('change', async () => {
  state.settings = await window.verbatim.setSettings({ numSpeakers: Number(els.selSpeakers.value) });
});
els.btnModelsFolder.addEventListener('click', () => window.verbatim.openModelsFolder());

/* ------------------------------------------------------------ keyboard -- */

document.addEventListener('keydown', (e) => {
  const typing = /INPUT|SELECT|TEXTAREA/.test(document.activeElement?.tagName || '') ||
    document.activeElement?.isContentEditable;
  if ((e.metaKey || e.ctrlKey) && e.key === 'f' && !els.viewTranscript.hidden) {
    e.preventDefault();
    els.search.focus();
    els.search.select();
    return;
  }
  if (typing) return;
  if (e.code === 'Space' && !els.viewTranscript.hidden) {
    e.preventDefault();
    els.btnPlay.click();
  }
});

/* ----------------------------------------------------------------- boot -- */

refreshHome().then(async () => {
  showView('home');
  // dev-only hook: VERBATIM_TEST_AUDIO=<path> auto-transcribes on launch
  const st = await window.verbatim.getState();
  if (st.testAudioPath) {
    const bytes = await window.verbatim.readFile(st.testAudioPath);
    const name = st.testAudioPath.split(/[\\/]/).pop();
    transcribeBlob(new Blob([bytes]), name, st.testAudioPath);
  }
});
