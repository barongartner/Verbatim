# Changelog

All notable changes to Verbatim. Versioning follows [SemVer](https://semver.org).

## [1.1.0] — 2026-07-10

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
