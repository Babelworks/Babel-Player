# Headless CLI: --dub and --tui

Both flags run the same pipeline engine as the desktop app without opening a
window. Exit codes: 0 success, 1 bad arguments, 2 pipeline failure,
130 cancelled.

## --dub (non-interactive)

```text
BabelPlayer.exe --dub --media <path> [--lang <code>] [--out <dir>]
  [--tts <provider>] [--voice <id>] [--no-diarization] [--no-mp4]
  [--consent-clone]
```

Examples:

```text
BabelPlayer.exe --dub --media clip.mp4 --lang es
BabelPlayer.exe --dub --media clip.mp4 --no-diarization --no-mp4
BabelPlayer.exe --dub --media clip.mp4 --tts chatterbox --consent-clone
```

`--tts` and `--voice` override the saved settings for this run only. When the
effective TTS provider is Chatterbox, `--consent-clone` is mandatory and is
not persisted. When a TTS override is given and the session already reached
the Translated stage, TTS re-runs under the requested provider and voice.

Every run writes `{stem}-captions.srt`, `{stem}-dub.mp3`, `{stem}-dub.mp4`
(unless `--no-mp4`), plus a `{stem}-dub.manifest.json` sidecar recording the
providers, voice, language, segment count, timestamps, and exit code.

## --tui (interactive menu)

```text
BabelPlayer.exe --tui [--media <path>] [--lang <code>] [--tts <provider>]
  [--voice <id>] [--out <dir>] [--no-diarization] [--no-mp4] [--consent-clone]
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
