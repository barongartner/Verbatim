# Changelog

All notable changes to Verbatim. Versioning follows [SemVer](https://semver.org).

## [2.1.0] — 2026-07-10

### Added
- **Fix the diarizer's mistakes**: right-click any line to reassign it to
  another speaker (or a brand-new one), copy it, or delete it; right-click a
  speaker chip to merge that speaker into another.
- **Search your whole library** from the home screen — titles, speaker names,
  and every word of every transcript, with match snippets.
- **Word export** (.docx) with colored speaker names and timestamps.
- **Playback speed is back** (0.75×–2×), now pitch-preserving.
- **Arrow-key seeking**: ←/→ skip 5 seconds during playback.

## [2.0.0] — 2026-07-10

**Full rewrite as a native Windows app** (C#/.NET 9 WinForms, same family as
Photon), replacing the Electron version.

### Changed
- Single portable `Verbatim.exe` (~120 MB) with the transcription engines
  embedded, plus a proper installer — both built by GitHub Actions.
- Same dark UI, same features: named speakers, click-to-jump playback with
  waveform, live transcript follow, search with in-text highlights, inline
  editing, TXT/SRT/VTT/JSON export, auto-saved library, URL transcription
  via yt-dlp with the Settings update button.
- Your existing library, settings, and downloaded models carry over — the
  on-disk format and location (`%APPDATA%\Verbatim`) are unchanged from 1.x.
- Audio decoding now uses Windows Media Foundation: MP3, WAV, M4A, AAC, WMA,
  FLAC (OGG/OPUS/WEBM need the free "Web Media Extensions" from the Microsoft
  Store; URL fetches prefer M4A automatically).

### Removed
- **macOS support.** The Electron line ended with v1.1.0, which remains
  downloadable.
- Playback speed selector (returning in a future release).

## [1.1.0] — 2026-07-10 *(final Electron release)*

### Added
- **Transcribe from a link.** Paste a YouTube URL, a podcast episode link, or
  any direct audio/video URL on the home screen and Verbatim fetches the audio
  and transcribes it — powered by [yt-dlp](https://github.com/yt-dlp/yt-dlp)
  (supports thousands of sites), downloaded automatically on first use.
- Fetched audio is kept in the app's library storage so playback and
  click-to-jump keep working later; it is cleaned up when the transcript is
  deleted.
- Settings → "Link downloader" shows the yt-dlp version with a one-click
  **Update** button (sites change their players often — update if links stop
  working).
- Non-speech markers from Whisper ("[Music]", "(applause)", "♪") are filtered
  out of transcripts.

### Guardrails
- Live streams are refused; links longer than 4 hours or larger than 1.5 GB
  are refused before download.

## [1.0.0] — 2026-07-10

Initial release: free, private, offline transcription for Windows and macOS.

- Whisper speech recognition (Tiny/Base/Small) + pyannote speaker diarization,
  fully local via sherpa-onnx; ~30 languages with auto-detect.
- Named speakers, click-to-jump playback with waveform, live transcript
  follow, search with match navigation, inline text editing.
- Exports: TXT, SRT, VTT, JSON, clipboard, portable project files.
- Auto-saved transcript library.
- Formats: MP3, WAV, M4A, AAC, FLAC, OGG, OPUS, WEBM.
