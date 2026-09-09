# Babel Player — AI Context

> Purpose: one current repo-context file for contributors and agents.
> Last verified against the codebase: 2026-04-18.

---

## Document Hierarchy

Use these files in this order:

1. `AGENTS.md`
   Repo rules, learned preferences, workspace facts, and testing constraints.
2. `docs/AI-CONTEXT.md`
   Current repo structure, provider matrix, commands, and artifact locations.
3. `docs/architecture.md`
   Structural boundaries and state ownership rules.
4. `docs/PLAN.md`
   Canonical docs map, current status summary, and retired-plan index.
5. `docs/Engineering-Plan.md`
   Current engineering status and active follow-up.

Dated plan and milestone documents are historical unless `docs/PLAN.md` explicitly says otherwise.

---

## Product Summary

Babel Player is a Windows desktop workstation for segment-based dubbing:

```text
Load Media -> Timed Transcript -> Optional Diarization -> Translation -> Dubbed Audio -> Preview -> Export
```

The app is built around a persistent session and artifact workflow rather than one-shot inference. Users should be able to reopen work, regenerate only the parts they want, and preview results in context.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Desktop UI | C# / .NET 10 + Avalonia 12.0.1 |
| MVVM | CommunityToolkit.Mvvm 8.4.2 |
| Playback | `libmpv` |
| Media processing | `ffmpeg` |
| Python env management | bundled `uv.exe` |
| Local inference host | FastAPI in `inference/main.py` |
| Tests | xUnit 2.9.3 |

Windows is the supported runtime. The repo carries `x64` and `ARM64` Windows paths. Linux and macOS are not current product targets.

---

## Current Runtime Posture

- Public compute profiles are `CPU`, `GPU`, and `Cloud`.
- The default GPU path is the managed local GPU host at `http://127.0.0.1:18000`.
- Docker is still supported as an advanced optional local GPU backend through the same HTTP contract.
- The desktop app does not treat Docker as the primary runtime model.
- Diarization is off by default until a provider is selected.
- Canonical persisted language codes are lowercase ISO 639-1 where the pipeline expects them.

Relevant types:

- `Models/ComputeProfile.cs`
- `Models/GpuHostBackend.cs`
- `Services/Settings/AppSettings.cs`

---

## Provider Matrix

This matrix is based on the active registries in `Services/Registries/`.

### Transcription

| Provider ID | Display | Compute |
|---|---|---|
| `faster-whisper` | Faster Whisper | CPU, GPU |
| `parakeet` | Parakeet TDT 0.6B | GPU |
| `openai-whisper-api` | OpenAI Whisper API | Cloud |
| `google-stt` | Google STT | Cloud |
| `gemini-transcription` | Google Gemini | Cloud |

### Translation

| Provider ID | Display | Compute |
|---|---|---|
| `ctranslate2` | CTranslate2 | CPU |
| `nllb-200` | NLLB-200 | GPU |
| `deepl` | DeepL API | Cloud |
| `openai` | OpenAI API | Cloud |
| `gemini-translation` | Google Gemini | Cloud |

### TTS

| Provider ID | Display | Compute |
|---|---|---|
| `piper` | Piper | CPU |
| `qwen-tts` | Qwen3-TTS | GPU |
| `edge-tts` | Edge TTS | Cloud |
| `elevenlabs` | ElevenLabs API | Cloud |
| `openai-tts` | OpenAI TTS | Cloud |

### Diarization

| Provider ID | Display | Compute |
|---|---|---|
| `wespeaker-local` | WeSpeaker | CPU |
| `sortformer-local` | SortFormer | CPU (ONNX) |
| `nemo-local` | NeMo | GPU |

Notes:

- XTTS is not part of the active TTS pipeline.
- Google Cloud TTS has code paths but is not surfaced in the current TTS registry UI list.
- WeSpeaker is the default managed CPU diarization path when multi-speaker detection is enabled. SortFormer is a selectable CPU ONNX alternative (max 4 speakers). The old GPU-hosted WeSpeaker endpoint is retired.

---

## Workflow Stages

`Models/SessionWorkflowStage.cs` defines:

1. `Foundation`
2. `MediaLoaded`
3. `Transcribed`
4. `Diarized`
5. `Translated`
6. `TtsGenerated`

Preview and export are downstream capabilities built on persisted session artifacts, not extra workflow stages.

---

## Current Architecture Shape

### State owner

`Services/SessionWorkflowCoordinator.cs` is the primary owner of workflow and session state.

- The root file is currently 1064 lines.
- Responsibilities are split across partials such as:
  - `SessionWorkflowCoordinator.Pipeline.cs`
  - `SessionWorkflowCoordinator.Playback.cs`
  - `SessionWorkflowCoordinator.Export.cs`
  - `SessionWorkflowCoordinator.TtsReference.cs`
  - `SessionWorkflowCoordinator.Orchestrators.Streaming.cs`

### Playback view-model split

`ViewModels/EmbeddedPlaybackViewModel.cs` is now a thinner composition root rather than a monolith.

Key view-models:

- `EmbeddedPlaybackViewModel.cs`
- `EmbeddedPlaybackPreviewViewModel.cs`
- `EmbeddedPlaybackPipelineViewModel.cs`
- `EmbeddedPlaybackSpeakerRoutingViewModel.cs`
- `SpeakerReferenceWizardViewModel.cs`

### Streaming pipeline

Streaming overlap is implemented in `SessionWorkflowCoordinator.Orchestrators.Streaming.cs` using `System.Threading.Channels`.

That path can overlap:

- transcription output
- translation consumption
- per-segment TTS generation

The repo should not document the pipeline as purely sequential anymore.

---

## Project Structure

```text
Babel-Player/
├── Assets/
├── Converters/
├── Models/
├── Services/
│   └── Registries/
├── Styles/
├── ViewModels/
├── Views/
├── BabelPlayer.Tests/
├── docs/
│   ├── AI-CONTEXT.md
│   ├── architecture.md
│   ├── PLAN.md
│   ├── Engineering-Plan.md
│   ├── history/
│   └── context/
├── inference/
├── native/
├── scripts/
├── tools/
├── AGENTS.md
├── CONTRIBUTING.md
├── README.md
└── BabelPlayer.csproj
```

---

## Key Files

| File | Role |
|---|---|
| `BabelPlayer.csproj` | Main desktop project; `net10.0`, `WinExe`, Avalonia 12.0.1 |
| `App.axaml.cs` | Composition root and startup/bootstrap wiring |
| `Models/CoordinatorCoreServices.cs` | Groups the coordinator's core service dependencies |
| `Services/SessionWorkflowCoordinator.cs` | Main workflow/session owner |
| `Services/Registries/TranscriptionRegistry.cs` | Active transcription provider matrix |
| `Services/Registries/TranslationRegistry.cs` | Active translation provider matrix |
| `Services/Registries/TtsRegistry.cs` | Active TTS provider matrix |
| `Services/Registries/DiarizationRegistry.cs` | Active diarization provider matrix |
| `Services/ManagedVenvHostManager.cs` | Managed local GPU host bootstrapping |
| `Services/ManagedCpuRuntimeManager.cs` | Managed CPU runtime bootstrapping |
| `ViewModels/MainWindowViewModel.cs` | Main shell composition |
| `ViewModels/EmbeddedPlaybackPipelineViewModel.cs` | Pipeline commands and progress state |
| `ViewModels/SpeakerReferenceWizardViewModel.cs` | Per-speaker references and voice assignment |
| `Views/MainWindow.axaml` | Main workflow UI |
| `inference/main.py` | FastAPI inference host |
| `scripts/check-architecture.py` | Structural and test-hygiene guard |

---

## Source Build and Verification

### Build

```powershell
pwsh ./scripts/fetch-win-native-deps.ps1
dotnet build Babel-Player.sln
dotnet run --project BabelPlayer.csproj -c Dev
```

Use `-SkipFfmpeg` only when you intentionally want to fetch `libmpv` and `uv` without `ffmpeg`/`ffprobe`.

### Maintained verification

```powershell
dotnet build Babel-Player.sln -c Release
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
python scripts/check-architecture.py
python -m py_compile inference/main.py
```

Optional smoke subset:

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "Category=Smoke"
```

Before changing tests, read `docs/testing-requirements.md`.

---

## Test Policy Summary

- `BabelPlayer.Tests` is the maintained suite.
- Keep it fast and deterministic.
- Do not add real Python, ffmpeg, container, libmpv, network, manual, or performance-dependent tests to the compiled suite.
- Quarantined tests live under `BabelPlayer.Tests/Quarantined/`.

---

## Artifact and Runtime Locations

| What | Where |
|---|---|
| Session state | `%LOCALAPPDATA%/BabelPlayer/state/` |
| Session artifacts | `%LOCALAPPDATA%/BabelPlayer/sessions/{sessionId}/` |
| Logs | `%LOCALAPPDATA%/BabelPlayer/logs/babel-player.log` |
| Python runtimes | `%LOCALAPPDATA%/BabelPlayer/runtime/` |
| Bundled Windows tools | `tools/<rid>/` |
| Native playback deps | `native/<rid>/` |

Transcript JSON filenames are based on the ingested source media stem, even if vocal separation creates intermediate files.

---

## Exports

Current export surfaces in `Views/MainWindow.axaml` and `Views/MainWindow.axaml.cs`:

- captions to `.srt`
- dubbed audio to `.mp3`
- source video plus dubbed track to `.mp4`

Supporting services:

- `Services/SessionWorkflowCoordinator.Export.cs`
- `Services/VideoExportPlanner.cs`
- `Services/FfmpegVideoExportRunner.cs`

Do not document video export as missing.

---

## Language and Voice Facts

- The curated local dub target set is 16 languages.
- Piper ships an in-app voice catalog for 14 of them.
- `ja` and `ko` currently rely on non-Piper TTS options.
- The Speaker Reference Wizard is the intended UX for per-speaker voice assignment and Qwen reference clips.

---

## Current Engineering Status

The repo now reflects:

- a working end-to-end dubbing pipeline
- decomposed playback and pipeline view-models
- channel-based streaming orchestration
- managed local GPU hosting as the default GPU path
- export support for `.srt`, `.mp3`, and `.mp4`

Active follow-up has shifted to hardening and UX, not missing core stages. See `docs/Engineering-Plan.md` and `docs/Next-Priorities-2026-04-16.md`.
