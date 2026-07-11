<img src="assets/banner.svg" alt="Verbatim — every word • every speaker • on your machine" width="100%" />

# Verbatim

**Every word, on your machine.** Free, private, offline audio transcription for
Windows — with automatic speaker identification.

[![Build](https://github.com/barongartner/Verbatim/actions/workflows/build.yml/badge.svg)](https://github.com/barongartner/Verbatim/actions/workflows/build.yml)

Drop in an MP3, WAV, M4A, AAC, WMA, or FLAC recording — or paste a
YouTube/podcast/any audio link — and Verbatim gives you a searchable,
clickable transcript:

- **Speaker detection** — figures out who spoke when, and you can give each
  speaker a real name (applies across the whole transcript).
- **Click to jump** — click any line's timestamp to hear that exact moment;
  the transcript follows along while the audio plays, with a waveform seek bar.
- **Search** — find any word or phrase (`Ctrl+F`), hop between matches, jump
  the audio straight to them.
- **Edit** — double-click any line to fix a mis-heard word; rename the
  transcript; everything auto-saves to your library.
- **Transcribe from a link** — paste a YouTube URL, podcast episode, or any
  direct audio/video link; Verbatim fetches the audio (via
  [yt-dlp](https://github.com/yt-dlp/yt-dlp), downloaded on first use) and
  transcribes it like any local file.
- **Export** — TXT, SRT, VTT, or JSON, or copy the whole transcript.
- **100% local transcription** — audio never leaves your computer. No account,
  no cloud, no fees. Network is used only for one-time model downloads and
  links you explicitly paste.

## How it works

Verbatim is a native Windows app (C#/.NET 9, WinForms — same family as
[Photon](https://github.com/The-Berin/Photon)). It runs
[OpenAI Whisper](https://github.com/openai/whisper) speech recognition and
[pyannote](https://github.com/pyannote/pyannote-audio) speaker segmentation
locally through bundled [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)
engines. On first transcription it downloads the speech models (~250 MB, one
time); after that it is fully offline.

Three model sizes are available in Settings: **Tiny** (fastest), **Base**
(recommended), and **Small** (most accurate). Around 30 languages are
supported, with automatic language detection.

## Install

Windows 10 1803 or later, 64-bit.

- **Installer**: run `VerbatimSetup-<version>.exe` from the
  [latest release](https://github.com/barongartner/Verbatim/releases/latest).
- **Portable**: grab `Verbatim.exe` — single file, no install.

Builds are unsigned (I am NOT paying for a code-signing certificate), so
SmartScreen may ask once: "More info" → "Run anyway".

> Looking for the old cross-platform (Electron) version, including the last
> macOS build? That line ended with
> [v1.1.0](https://github.com/barongartner/Verbatim/releases/tag/v1.1.0).

## Development

```bash
dotnet test Verbatim.sln              # unit tests everywhere; on macOS this also
                                      # runs the real pipeline against vendor/mac
dotnet publish src/Verbatim/Verbatim.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true           # portable Verbatim.exe
```

`Verbatim.Core` (engine orchestration, models, stores, exports) is
cross-platform and fully covered by tests; `Verbatim` is the WinForms shell.
The sherpa-onnx engines are embedded in the exe and unpacked to
`%LOCALAPPDATA%\Verbatim\engines` on first run. Releases are built by GitHub
Actions from tags (`v*`), matching the Photon pipeline.

## License

MIT — part of The-Berin's free software line, alongside
[Photon](https://github.com/The-Berin/Photon) and
[ReType](https://github.com/The-Berin/ReType).
