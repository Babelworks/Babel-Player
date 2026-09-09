# Copilot Instructions for Babel Player

Before non-trivial work, read in this order: `AGENTS.md`, `docs/AI-CONTEXT.md`, `docs/architecture.md`, `docs/PLAN.md`, then `docs/Engineering-Plan.md`. If you touch milestone-specific behavior, also check the relevant smoke note in `docs/history/smoke/`.

## Build, test, and lint commands

```bash
# Build the solution
dotnet build Babel-Player.sln

# Maintained day-to-day test suite (preferred)
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release

# Smoke subset
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "Category=Smoke"

# Run a single test class or method
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "FullyQualifiedName~SessionWorkflowCoordinatorUnitTests"
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "FullyQualifiedName~SessionWorkflowCoordinatorUnitTests.Initialize_NoSavedSnapshot_CreatesFoundationSession"

# Architecture linter
python3 scripts/check-architecture.py

# Python inference syntax check
python -m py_compile inference/main.py

# Run the desktop app in the Dev configuration
dotnet run -c Dev
```

Do **not** treat `dotnet test Babel-Player.sln` as the routine verification path. See `docs/testing-requirements.md` and `AGENTS.md`.

Test categories used in this repo:

- `Smoke`
- `Quarantined`
- `Integration` (not in the maintained compiled suite policy)
- `RequiresPython` / `RequiresFfmpeg` / `RequiresExternalTranslation` (do not add to maintained suite)

## High-level architecture

- `App.axaml.cs` is the composition root. It wires settings, credential storage, media transport, inference registries, runtime managers, and the main window view model. Persisted app/session data lives under `%LOCALAPPDATA%\BabelPlayer\`.
- `SessionWorkflowCoordinator` is the single owner of workflow/session state. It is split across partial files (`SessionWorkflowCoordinator*.cs`) but still acts as one boundary: media ingest, stage progression, session restore, playback coordination, runtime warmup state, and persistence all flow through it.
- The shell is intentionally thin. `ViewModels/` display coordinator state and send commands; they are not supposed to own pipeline logic or call stage services directly.
- Provider selection is stage-specific. `TranscriptionRegistry`, `TranslationRegistry`, and `TtsRegistry` expose available providers/models, readiness checks, and provider creation. `InferenceRuntimeCatalog` is the canonical place for mapping CPU/GPU/Cloud compute profiles to provider/runtime defaults and normalization.
- Media playback is split into two transports: `LibMpvEmbeddedTransport` for source video/audio preview and `LibMpvHeadlessTransport` for segment/TTS playback. Both are created and owned through `MediaTransportManager`.
- Persistence is layered:
  - `SessionSnapshotStore` handles the current-session snapshot and corruption recovery.
  - `PerSessionSnapshotStore` stores per-session snapshots and artifacts under the session ID directory.
  - `RecentSessionsStore` maintains the MRU session list.
  - Session restore validates artifacts and can downgrade the saved stage if files are missing.
- Python-backed inference stays behind explicit service/process boundaries. CPU-local providers use the managed runtime bootstrap path; GPU-local providers route through the managed/containerized host path rather than being called directly from the UI.

## Key conventions

- The workflow is a staged pipeline, not a collection of unrelated features. The canonical progression is `Foundation -> MediaLoaded -> Transcribed -> Diarized -> Translated -> TtsGenerated`.
- Stage advancement should go through coordinator entry points such as `AdvancePipelineAsync`, `ContinuePipelineAsync`, and `RunTtsOnlyAsync`. Do not wire ViewModels or UI code straight to raw pipeline stage methods.
- Treat `SessionWorkflowCoordinator` as the state owner. If a new feature needs durable workflow state, it likely belongs there rather than in a ViewModel-local cache.
- Provider identifiers and credential keys must come from `Models/ProviderNames.cs` (`ProviderNames.*`, `CredentialKeys.*`). The architecture linter explicitly rejects inline provider string literals in production code.
- The Python/C# boundary is an explicit serialization contract. Match field names deliberately with exact JSON names or `[JsonPropertyName]`; do not rely on implicit PascalCase conversion. Segment IDs follow the `segment_{start}` format and must stay stable because TTS segment artifacts are keyed by that ID.
- Use `InferenceRuntimeCatalog` for provider/profile/runtime normalization instead of duplicating compute-selection logic in UI or service code.
- Missing capability/readiness should surface as a truthful blocked state with remediation, not a fake-ready UI path. If you add a fallback, it must be explicit in status/logging.
- Keep storage and identity names consistent by context: product branding is `Babel Player`, the repo is `Babel-Player`, and persisted app paths/identifiers use `BabelPlayer`.
- **Avoid UI Verbosity:** Prefer concise, professional labels (e.g., "Ready", "Reset to [Stage]") over internal technical descriptions. Do not include instructional filler for standard UI elements.
- This repo is milestone-driven. Avoid broad refactors, speculative extension points, or “future-proof” abstractions unless they directly support the current work tracked in `docs/PLAN.md` / `docs/Next-Priorities-*.md`.
- For benchmark or hardware-routing work, capture the machine/runtime environment first and keep comparisons hardware-specific. Existing repo instructions expect an `Environment Snapshot` section and hardware-profile tokens such as `int8_8c16t_32g` in benchmark matrices.

## MCP server setup guidance

- **Avalonia/.NET docs MCP:** configure an MCP server that can answer against official Avalonia and Microsoft/.NET documentation. Use it for Avalonia control behavior, XAML/binding semantics, dispatcher/threading rules, .NET runtime APIs, and Windows/DirectML-related platform guidance.
- **Context7-style docs MCP:** configure a general package/framework documentation server for dependency lookups and examples. It is the right place for NuGet, Python, Docker, and library usage details that are not specific to this repository's local code.
- **DeepWiki MCP:** configure it as a secondary research source for architecture notes, repository overviews, and external project documentation when local code search is not enough. Treat local files in this repository as the source of truth when DeepWiki summaries and the checked-in code disagree.
- Prefer these MCP sources in this order:
  1. Local repository files for repo-specific behavior and conventions.
  2. Avalonia/.NET docs MCP for framework/platform questions.
  3. Context7-style docs MCP for package and library documentation.
  4. DeepWiki MCP for broader background or cross-repo reference material.
