## Repo Context
- For current repo truth, read in this order: `docs/AI-CONTEXT.md`, `AGENTS.md`, `docs/architecture.md`, `docs/PLAN.md`.
- Keep agent context files minimal; do not duplicate repo status or tool assumptions in them.

## Learned User Preferences
- Prefer iterative runtime debugging loops: reproduce issue, then continue from fresh logs.
- Prefer non-hardcoded debug log paths for instrumentation; use env var override with repo-root fallback.
- For continual-learning runs, require strict incremental transcript processing and high-signal-only memory updates.
- Prefer disabling/removing Cursor hooks that interrupt normal workflow (popups or cursor focus changes).
- Prefer canonical language codes in persisted settings and pipeline artifacts (typically lowercase ISO 639-1); keep human-readable labels only in UI catalogs to avoid provider contract and strict string-compare mismatches.
- Prefer custom window chrome that matches Windows 11 expectations: flush edge-to-edge segments, square full hit targets, and even alignment without ragged gaps between adjacent controls.
- For NVIDIA RTX Video, bias defaults toward enabling VSR and RTX HDR when hardware and gates allow; keep RTX HDR and HDR passthrough mutually exclusive in the UI; when RTX HDR is enabled, suppress or disable conflicting secondary HDR processing options (such as tone mapping) that should apply under passthrough instead.
- Prefer Piper and Edge TTS voice download and per-speaker voice assignment through the Speaker Reference Wizard rather than duplicating long voice lists in Settings or the main pipeline controls.
- For commit/push requests that provide an explicit staged/unstaged file list, treat that list as authoritative and do not include files outside it.
- When asked to implement from an attached plan with pre-created todos, execute the plan without editing the plan file and progress existing todos instead of creating duplicates.
- Prefer concise, plain UI wording; avoid em dashes in user-visible copy when a short phrase, comma, or “to” for ranges reads clearly (for example “30 to 60 seconds” instead of “30–60”).
- For advanced CPU transcription and similar parallelism controls, prefer hardware-informed defaults over fixed ultra-conservative baselines; when clamping values or falling back to a safer compute path after errors or incompatible choices, notify the user explicitly rather than only logging.
- **Avoid UI Verbosity:** Prefer concise, action-oriented status messages (e.g., 'Ready') over internal technical explanations or instructional filler. Hide the "sausage-making" from the primary workflow; keep diagnostic detail secondary or hidden. Do not over-explain basic UI actions (e.g., how "OK" buttons work).

## Learned Workspace Facts
- The desktop UI targets **Avalonia 12.x** as pinned in `BabelPlayer.csproj` (do not assume Avalonia 11 or other versions when discussing APIs or docs unless verified from the project file).
- The repository no longer uses Git LFS; LFS-related hook assumptions are outdated here.
- Docker support is maintained as a power-user inference-host option; containerizing the desktop app is not the primary runtime model.
- Forward-facing product naming uses `Babel Player` (space, no dash); dev builds append `[DEV]`.
- On Windows, client diagnostics are commonly written to `%LocalAppData%/BabelPlayer/logs/babel-player.log`.
- Per-provider language allowlists and multilingual capability tags are maintained in centralized catalog types in the codebase rather than ad hoc string checks scattered through the pipeline.
- NVIDIA RTX Video features (VSR, RTX HDR) are gated on supported GPU hardware, display HDR state where applicable, and the GPU-accelerated video path (for example `VideoUseGpuNext`-style settings), not on a single flag alone.
- Public project site/docs are served via GitHub Pages at `https://babelworks.github.io/Babel-Player/`.
- Windows native deps install **ffmpeg.exe** and **ffprobe.exe** under `tools/<rid>/`; the managed GPU host prepends those directories to **PATH** so subprocess audio tooling (for example pydub) can resolve **ffprobe**.
- Multi-speaker detection is off by default. Enabling it defaults to **WeSpeaker**; **SortFormer** (`sortformer-local`) is a selectable CPU ONNX alternative. Periodic NeMo background health on the GPU host is off unless **`BABEL_ENABLE_NEMO_BACKGROUND_HEALTH`** is enabled.
- Transcript JSON under `transcripts/` is named from the **ingested source media** stem even when vocal separation uses a generated stem (for example `vocals.wav`); **`VocalSeparationEnabled`** is coerced off when the container reports the audio separator is not ready.

## Testing Requirements
- Before writing or modifying tests, read `docs/testing-requirements.md`.
- The maintained suite command is `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release`; do not treat `dotnet test Babel-Player.sln` as the routine verification path.
- `BabelPlayer.Tests` must stay fast and deterministic. Do not add real Python, ffmpeg, container, libmpv, manual, or performance-dependent tests to the compiled suite.
- If a test is flaky, slow, or runtime-heavy, prefer deleting or quarantining it over preserving short-term nominal coverage.
- Do not add broad UI/workflow tests to compiled `BabelPlayer.Tests` just because they use fakes. `SessionWorkflowCoordinator*`, `*Orchestrator*`, `EmbeddedPlaybackPreview*`, and similar harness-style suites should default to `BabelPlayer.Tests/Quarantined/` unless they are unusually small, deterministic seam tests.

## Conventions
- Use `using` directives at the top of source files.
