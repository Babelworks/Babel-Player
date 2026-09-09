# Babel Player — Engineering Status

Last verified against the codebase: 2026-04-18

This is the maintained engineering status document. Dated milestone trackers and implementation plans are historical only.

## Current Summary

The repo now implements the main product loop:

```text
load media -> transcribe -> optional diarize -> translate -> generate dubbed speech -> preview -> export -> resume later
```

The codebase is no longer in the state described by the older April 2026 "remaining implementation" snapshots:

- playback view-model decomposition is done
- coordinator constructor cleanup landed
- channel-based streaming orchestration landed
- Qwen GPU TTS is active
- XTTS is not part of the active pipeline
- export is present for `.srt`, `.mp3`, and `.mp4`

## Runtime and Hosting Posture

- Public compute profiles are `CPU`, `GPU`, and `Cloud`.
- The default GPU path is the managed local GPU host.
- Docker remains an advanced optional GPU backend.
- Local CPU paths remain available for Faster Whisper, CTranslate2, Piper, WeSpeaker, and SortFormer.
- Local GPU paths currently cover Faster Whisper, Parakeet, NLLB-200, Qwen3-TTS, and NeMo.

## Active Provider Surface

### Transcription

- Faster Whisper: CPU and GPU
- Parakeet TDT 0.6B: GPU
- OpenAI Whisper API: Cloud
- Google STT: Cloud
- Google Gemini: Cloud

### Translation

- CTranslate2: CPU
- NLLB-200: GPU
- DeepL: Cloud
- OpenAI: Cloud
- Google Gemini: Cloud

### TTS

- Piper: CPU
- Qwen3-TTS: GPU
- Edge TTS: Cloud, no API key
- ElevenLabs: Cloud
- OpenAI TTS: Cloud

### Diarization

- WeSpeaker: CPU (default when multi-speaker detection is enabled)
- SortFormer: CPU ONNX (selectable, max 4 speakers)
- NeMo: GPU

## Current Structural State

- `SessionWorkflowCoordinator` remains the primary workflow and session owner.
- The coordinator is split across partials and no longer matches the older constructor-bloat plan docs.
- `EmbeddedPlaybackViewModel` is now a composition root with focused child view-models:
  - `EmbeddedPlaybackPreviewViewModel`
  - `EmbeddedPlaybackPipelineViewModel`
  - `EmbeddedPlaybackSpeakerRoutingViewModel`
- The streaming pipeline uses `System.Threading.Channels` in `SessionWorkflowCoordinator.Orchestrators.Streaming.cs`.

## Verification and Quality Posture

Routine maintained verification:

```powershell
dotnet build Babel-Player.sln -c Release
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
python scripts/check-architecture.py
python -m py_compile inference/main.py
```

The maintained suite is `BabelPlayer.Tests`, not `dotnet test Babel-Player.sln`.

## What Is Still Active Work

The main open work is follow-up, not missing core stages:

1. Gordon refactor Tier 2 and further coordinator/runtime cleanup.
2. More truthful fractional progress wiring for long-running pipeline stages.
3. Continued validation and hardening of runtime behavior on clean machines and alternate GPU backends.
4. Keeping historical docs and smoke notes aligned so they do not imply stale open gaps.

See [Next-Priorities-2026-04-16.md](Next-Priorities-2026-04-16.md) for the short active list.

## Historical References

These files are preserved for chronology and evidence, not as current status:

- [Remaining-Implementation-Plan-2026-04-12.md](history/planning/Remaining-Implementation-Plan-2026-04-12.md)
- [Milestones-Tracker-2026-04-08.md](history/planning/Milestones-Tracker-2026-04-08.md)
- [history/smoke/](history/smoke/)
