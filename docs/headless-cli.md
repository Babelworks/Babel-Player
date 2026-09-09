# Headless CLI: --dub and --tui

Both flags run the same pipeline engine as the desktop app without opening a
window. Exit codes: 0 success, 1 bad arguments, 2 pipeline failure,
130 cancelled.

## --dub (non-interactive)

```text
BabelPlayer.exe --dub --media <path> [--lang <code>] [--out <dir>]
  [--tts <provider>] [--voice <id>] [--project-dir <dir>]
  [--diarization <provider>] [--no-diarization] [--no-mp4] [--consent-clone]
```

`--out <dir>` writes captions, dub audio, and the MP4 into that folder.
When omitted, both `--out` and `--project-dir` default to a `{filename}.babel`
folder next to the media file (for example `clip.mp4.babel`), matching the GUI.
`--project-dir <dir>` stores the run's session (snapshot, transcripts,
translations) in `<dir>/sessions`. Settings, credentials, and recent-session
history stay app-local. Turn the default off in Settings (Keep project folders
next to media) or pass an explicit `--project-dir`.

Examples:

```text
BabelPlayer.exe --dub --media clip.mp4 --lang es
BabelPlayer.exe --dub --media clip.mp4 --no-diarization --no-mp4
BabelPlayer.exe --dub --media clip.mp4 --tts chatterbox --consent-clone
BabelPlayer.exe --dub --media clip.mp4 --diarization sortformer-local --no-mp4
```

`--tts` and `--voice` override the saved settings for this run only. When the
effective TTS provider is Chatterbox, `--consent-clone` is mandatory and is
not persisted. When a TTS override is given and the session already reached
the Translated stage, TTS re-runs under the requested provider and voice.

`--diarization <provider>` selects a local diarization provider for this run
(for example `wespeaker-local` or `sortformer-local`). `--no-diarization`
clears diarization for the run and wins if both flags are passed. Unlike the
desktop UI, headless `--dub` does not pause after speaker mapping; it continues
straight into translation and dub.
Every run writes `{stem}-captions.srt`, `{stem}-dub.mp3`, `{stem}-dub.mp4`
(unless `--no-mp4`), plus a `{stem}-dub.manifest.json` sidecar recording the
providers, voice, language, segment count, timestamps, and exit code.

## --tui (interactive menu)

```text
BabelPlayer.exe --tui [--media <path>] [--lang <code>] [--tts <provider>]
  [--voice <id>] [--out <dir>] [--project-dir <dir>]
  [--diarization <provider>] [--no-diarization] [--no-mp4] [--consent-clone]
```

Staged setup asks only for what the flags did not answer, prints the effective
configuration (saved defaults included), then runs the same engine as `--dub`.
The process exits with the pipeline exit code after a run; quitting from the
menu exits 0. Prompts go to stderr and results to stdout, so sessions are
scriptable:

```text
"1" | BabelPlayer.exe --tui --media clip.mp4 --lang es --tts piper --no-diarization
```

The media picker never changes the process working directory. Voice cloning
consent is requested whenever Chatterbox is the effective provider, including
when it comes from the saved default.
