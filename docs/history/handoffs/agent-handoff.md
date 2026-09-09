# Agent Handoff — Babel Player Alpha

> **Historical handoff** (written 2026-09-08 UTC). Point-in-time snapshot only.
> Canonical docs: `AGENTS.md` → `docs/AI-CONTEXT.md` → `docs/architecture.md` → `docs/PLAN.md`.
> Verify PR states with `gh` before acting on anything below.

## 1. What this is

Babel Player (`BabelPlayer.csproj`, .NET 10 + Avalonia 12) is a Windows desktop
AI-dubbing workstation: load media → transcribe → (diarize) → translate → per-segment
TTS → preview → export (SRT/MP3/MP4). Public alpha at
**https://github.com/trackdubllc/Babel-Player-Alpha** (org: `trackdubllc`).
Local checkout: `D:\Dev\Babel-Player-Proto` (folder rename to match GitHub was
skipped — a live handle blocks it; cosmetic only, nothing references the path).

Docs map: `AGENTS.md` → `docs/AI-CONTEXT.md` → `docs/architecture.md` →
`docs/reference/babel-2.0-tenets.md` → `docs/plans/cloud/aws-offload-lane.md` (Phase 3).

## 2. Where things stand

- **v1.3.0 RELEASED** (public, full release not draft): installer + win-x64/arm64
  portable zips + SHA256 at `/releases/tag/v1.3.0`. First release verified E2E
  before shipping (CPU lane, twice, real artifacts).
- **Merged to main:** #275 (Phase 0 E2E fixes), #286 (piper publish), #287 (pin
  revert + seam tests), #288 (APM fix), #289 (tenets doc).
- **OPEN: #290** — Chatterbox multilingual voice cloning (ONNX, CPU-first) on
  branch `feature/chatterbox-voice-cloning` (local tip `2b6e559`, pushed, tree clean).
  All 15 macroscope review threads addressed (11 fixed, 2 rebutted with rationale)
  and marked resolved. CI re-ran on the push; merge when green.
- **Suite:** 806/806 green (`dotnet test BabelPlayer.Tests/... -c Release`).
- **E2E proof:** `clip.mp4` (ES→FR) and `birdsbees.mp4` (FR→FR) fully green via the
  headless CLI; voice-cloned French-in-Spanish-voice dub verified real
  (15.67s, -18 dB mean, soft-subbed MP4).

## 3. Locked decisions (do not relitigate without new evidence)

- No Go/sidecar rewrite of inference; no third engine repo. C# supervises, models
  run where they run.
- Chatterbox-first port order (smallest engine, third-party MIT models, 22 langs),
  then CosyVoice, then Qwen3 with GPU. (CosyVoice was briefly dropped 2026-06-11
  as "superseded by Qwen" and re-added 4 days later with full audit — nothing was
  ever dropped for quality reasons.)
- `zh` stays rejected until the Cangjie preprocessing pipeline exists (model needs it).
- WAV bytes in `.mp3`-named segment files matches Piper precedent; all downstream
  consumers content-sniff. Revisit for both providers together or not at all.
- No voice→language gating. Default voice may follow target language; manual pick wins.
- Docker backend is dead weight: unmaintained image, pull failures on startup, zero
  users. Remove when the ONNX migration makes it redundant.
- R2 for future artifact hosting. Public alpha under `trackdubllc`; brand
  entanglement with TrackDub accepted.
- Dependabot must never auto-land major ML bumps again (see §5).

## 4. Next work, in order

1. **Land #290** (merge when CI green), then tag **v1.4** with cloning. That release
   is the "premium tier without Python" milestone.
2. **SortFormer diarization port** (NVIDIA streaming SortFormer ONNX is on
   `tonythethompson` HF). Kills the last Python-adjacent dep and the red provider
   card. Same playbook as Chatterbox: engine + provider seam + manifest + `--dub`
   verification.
3. **Whisper/NLLB-or-MADLAD ONNX migration** → torch venv retires → Docker backend
   deleted → startup dialog deleted with it.
4. **Settings checkbox** for `ChatterboxVoiceCloneConsent` (setting exists, CLI flag
   exists, GUI toggle missing). **Strategic integration milestone:** Introduce 
   `Zafiro.Avalonia` and `ReactiveUI.SourceGenerators` here to build the unified UI configuration 
   block, keeping presentation plumbing completely isolated from background inference services.
5. **GPU track**: fix `HardwareSnapshot` reporting `cuda=no` on the RTX 5070 (it
   detects via the CPU-only torch venv instead of the driver), then TRT-RTX EP
   plugin route per the tenets (manifest → fetch → explicit EP selection → honest
   fallback ladder). User hardware: RTX 5070 Blackwell 12GB, driver 616.x, CUDA 13.3.
6. **Advanced Segment-Timeline Canvas UI**: Prototype the SkiaSharp timeline track editor 
   rendering loop. Leverage `System.Reactive` exclusively here to sample pointer-scrubbing 
   events (`.Sample(TimeSpan.FromMilliseconds(16))`) to prevent high-frequency UI events from 
   flooding unmanaged ONNX runtime inference sessions or choking thread allocation.
7. **Deepgram provider** (Nova-3 ASR) — clean `ITranscriptionProvider` seam job.
8. **Phase 3 AWS lane** per `docs/plans/cloud/aws-offload-lane.md` (Batch shape, G5/G6 bench,
   Spot, S3 layout, Bedrock workloads) — co-design with the partner team.
9. **Hygiene backlog**: triage 28 Dependabot alerts; `gemini-*` workflows need
   secrets or disabling (user doesn't know what they are — recommend disable);
   docs site still on `babelworks.github.io`; pending torch-2.13.0 bump branch.

## 5. Hazards (learned the hard way)

- **Dependabot majors rot the venv.** transformers 4.46.3→5.10.1 auto-merged, the
  bootstrap marker rebuilt the world overnight, `HubertModel` removal killed every
  provider. Pins live in `inference/*-constraints.txt`; treat any major bump as a
  full-E2E-gated event. Torch 2.13.0 bump is still pending out there.
- **The runtime reads pins from build output** (`bin/**/inference/`), not the repo.
  Editing `inference/` requires a rebuild to take effect.
- **Branch protection**: PR-only to `main` (+ Copilot review). `gh` CLI auth works
  (user `tonythethompson`, ADMIN). MCP GitHub tools use a weaker token (no admin) —
  prefer `gh` for repo operations. Never force-push.
- **Concurrent agents**: Cursor background agents + other opencode sessions are
  active on this machine and HAVE edited files mid-session (a stray line appeared
  in `PythonSubprocessServiceBase.cs`; a commit landed on a PR branch). Verify file
  state (read before edit), keep commits small, expect remote branches to move.
- **Tool timeouts on long runs**: launch headless runs via `Start-Process` (the tool
  may report kill-errors even on success — verify via `Get-Process` + log tails,
  never assume). Poll with short commands; check `dub.log` (headless) and
  `babel-player.log` (GUI).
- **One venv, one writer at a time**: concurrent pipeline runs race on model files
  (observed sharing violation on a `.tmp`). Serialize runs.
- **Fresh clones crash without `Assets/logo x512.png`** — fixed by tracking it;
  do not re-ignore generated assets that XAML references.
- **Session restore quirk**: `LoadMedia` may present a cached session at an older
  stage; `--tts` forces TTS re-run via reset-to-Translated. Stale journals from
  failed runs are now reset per run (don't "fix" that back).

## 6. How to verify (the loop that works)

```powershell
# Full E2E on CPU (drives the REAL coordinator, writes real artifacts):
.\bin\Dev\net10.0\BabelPlayer.exe --dub --media "B:\OneDrive\Videos\Movies\clip.mp4"
# Voice cloning:
.\bin\Dev\net10.0\BabelPlayer.exe --dub --media <clip> --tts chatterbox --consent-clone
```
- Corpus: `B:\OneDrive\Videos\Movies` (short AI-generated test clips).
- Logs: `%LOCALAPPDATA%\BabelPlayer\logs\dub.log` (CLI), `babel-player.log` (GUI).
- Runtimes/models: `%LOCALAPPDATA%\BabelPlayer\runtime\managed-cpu\.venv`,
  `%LOCALAPPDATA%\BabelPlayer\models\chatterbox-multilingual` (9 files),
  `%LOCALAPPDATA%\piper\voices`.
- Suite: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release`
  (fast/deterministic only — no real Python, network, sleeps >100ms; see
  `docs/testing-requirements.md`; harness-style suites go to `Quarantined/`).
- Release cut: merge to main → `git tag -a vX.Y.Z` → push tag → `Release Windows`
  workflow builds matrix + installer → auto-creates the GitHub Release.

## 7. Reference map

- WinUI-era TrackDub (closest-to-finished ancestor, working voice cloning):
  tag `winui-final` (= `d44b61e8`) in `D:\Dev\Trackdub-Monorepo-Archive`
  (+ GitHub `trackdubllc/Trackdub-Monorepo-Archive`).
- TrackDub ONNX engines to port from: `D:\Dev\Trackdub_Workspace\Trackdub\src\Trackdub.Inference.Onnx`
  (Chatterbox done; CosyVoice/Qwen3/SortFormer/Whisper next).
- User's HF inventory (`tonythethompson`, 34 models) already holds nearly all needed
  ONNX bundles (CosyVoice, Qwen3-TTS/ASR, Whisper-GenAI, SortFormer, Silero, Kokoro,
  MADLAD, Phi, LatentSync). Chatterbox comes from `ResembleAI`/`onnx-community` (MIT).
- Key Babel files: `Services/DubCli.cs` (headless driver), `Services/SessionWorkflowCoordinator*`
  (state owner), `Services/Chatterbox/` (port reference implementation),
  `Services/PythonJsonWorkerPool.cs` + `PythonSubprocessServiceBase.cs` (UTF-8 stdio),
  `Services/Registries/*` (provider matrix), `scripts/fetch-win-native-deps.ps1`,
  `Directory.Build.targets` + `BabelPlayer.csproj` (native staging).
- Partner context: AWS SA thread (Batch/G5-G6/Spot, Bedrock case #178778897900076),
  Deepgram (cloud ASR candidate). Details in `docs/plans/cloud/aws-offload-lane.md`.

## 8. Open questions for the human

- Merge #290 now or wait for another review round?
- v1.4 scope: cloning only, or bundle SortForward diarization with it?
- `gemini-*` workflows: disable (recommended) or configure secrets?
- Confirm the `PYTHONIOENCODING` line's author (appeared mid-session; kept as harmless).
