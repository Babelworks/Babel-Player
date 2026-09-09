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

## --tui (interactive menu)

```text
BabelPlayer.exe --tui [--media <path>] [--lang <code>]
```

Numbered menus collect the TTS provider, voice/model, diarization, and export
choices, print the effective configuration (saved defaults included), then run
the same engine as `--dub`. The process exits with the pipeline exit code
after a run; quitting from the menu exits 0. Menus read stdin lines, so
sessions are scriptable:

```text
"1","2","","n","y","" | BabelPlayer.exe --tui --media clip.mp4 --lang es
```

The media picker never changes the process working directory. Voice cloning
consent is requested whenever Chatterbox is the effective provider, including
when it comes from the saved default.
