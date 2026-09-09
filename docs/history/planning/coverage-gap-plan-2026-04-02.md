# Coverage Gap Plan (2026-04-02)

## Progress Update (2026-04-02)
- Stability status: resolved for the earlier failing clusters; full test project currently passes (337 passed, 0 failed).
- Implemented first Priority A slice:
  - Added deterministic containerized provider tests in [BabelPlayer.Tests/ContainerizedProvidersTests.cs](BabelPlayer.Tests/ContainerizedProvidersTests.cs).
  - Added containerized probe behavior tests in [BabelPlayer.Tests/ContainerizedServiceProbeTests.cs](BabelPlayer.Tests/ContainerizedServiceProbeTests.cs).
  - Added injectable HttpClient support to [Services/ContainerizedInferenceClient.cs](Services/ContainerizedInferenceClient.cs) to enable offline HTTP-path testing.
- Targeted coverage deltas from the new test slice:
  - [Services/ContainerizedTranslationProvider.cs](Services/ContainerizedTranslationProvider.cs): 89.8%
  - [Services/ContainerizedTtsProvider.cs](Services/ContainerizedTtsProvider.cs): 88.2%
  - [Services/ContainerizedTranscriptionProvider.cs](Services/ContainerizedTranscriptionProvider.cs): 93.5%
  - [Services/ContainerizedInferenceClient.cs](Services/ContainerizedInferenceClient.cs): 75.0%
  - [Services/ContainerizedProviderReadiness.cs](Services/ContainerizedProviderReadiness.cs): 81.4%
  - [Services/ContainerizedServiceProbe.cs](Services/ContainerizedServiceProbe.cs): 86.8%
- Remaining Priority A gap:
  - [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs) still needs more invalidation/synchronization path coverage.

- Implemented ViewModel switching behavior tests in [BabelPlayer.Tests/SessionWorkflowTests.cs](BabelPlayer.Tests/SessionWorkflowTests.cs):
  - runtime switches for transcription/translation/TTS
  - provider->model reconciliation for translation and TTS
- Updated targeted coverage:
  - [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs): 40.2% (up from 28.4%)

### Next Implementation Slice
1. Add probe-focused tests for:
- cache hit behavior (available/unavailable TTL)
- in-flight probe reuse
- wait-timeout returns checking state
- force-refresh bypasses cache
2. Add focused ViewModel invalidation/synchronization tests for [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs).
3. Re-run coverage and update this plan with measured deltas.

## Baseline From Coverage Run
- Command: run full test suite in coverage mode.
- Result: 282 passed, 23 failed.
- Important note: coverage percentages are useful for prioritization, but final gate decisions should happen after stabilizing failing tests.

## Immediate Stability Blockers (Fix First)
1. Registry behavior expectation drift
- Failing tests indicate unknown provider behavior changed (likely due runtime/provider normalization paths).
- Affected tests include unknown-provider readiness and create-provider throw assertions in RegistryTests.
- Action:
  - Decide and document intended contract for unknown providers in each registry.
  - Align tests OR implementation to that contract consistently for Transcription/Translation/TTS registries.

2. Exception contract drift in provider tests
- Multiple tests expect ArgumentException but current implementation throws FileNotFoundException or ArgumentNullException.
- Cancellation tests expect OperationCanceledException but observe TaskCanceledException.
- Action:
  - Standardize input validation at provider entry points before subprocess/file operations.
  - For cancellation assertions, use Assert.ThrowsAnyAsync<OperationCanceledException> where appropriate, or normalize thrown cancellation exception type in service layer.

3. DeepL constructor + request validation mismatch
- Tests expecting constructor-time API key validation currently fail.
- Some tests hit live-flow exception types before validation is enforced.
- Action:
  - Move/restore defensive argument checks at constructor/method boundaries so behavior is deterministic and testable.

## Coverage Priorities (Production Code)

### Priority A: Core pipeline and runtime routing
Target files:
- Services/ContainerizedTranscriptionProvider.cs (0.0%)
- Services/ContainerizedTranslationProvider.cs (0.0%)
- Services/ContainerizedTtsProvider.cs (0.0%)
- Services/ContainerizedInferenceClient.cs (11.2%)
- Services/ContainerizedServiceProbe.cs (5.6%)
- ViewModels/EmbeddedPlaybackViewModel.cs (28.4%)

Plan:
1. Add focused unit tests for containerized providers:
- success path with mocked client responses
- missing input path validation
- artifact write/read invariants
- single-segment translation mutation path
- readiness pass/fail paths

2. Add tests for service probe state transitions:
- checking -> available
- unavailable with error detail
- capability missing branch per stage
- probe refresh/caching behavior

3. Add ViewModel behavior tests around pipeline setting changes:
- runtime/provider/model changes produce expected PipelineSettingsSelection
- invalidation triggers mode reset and segment refresh behavior
- runtime switch falls back to valid provider/model

Exit criteria:
- each containerized provider >= 70%
- ContainerizedInferenceClient >= 60%
- ContainerizedServiceProbe >= 60%
- EmbeddedPlaybackViewModel >= 50%

### Priority B: Cloud/local providers with low coverage
Target files:
- Services/OpenAiTranslationProvider.cs (6.4%)
- Services/PyannoteDiarizationProvider.cs (7.0%)
- Services/ModelDownloader.cs (12.8%)
- Services/HardwareSnapshot.cs (13.7%)
- Services/ElevenLabsTtsProvider.cs (15.8%)
- Services/DeepLTranslationProvider.cs (22.5%)
- Services/BootstrapDiagnostics.cs (25.0%)

Plan:
1. Create deterministic tests with fake subprocess/http boundaries for each provider.
2. Add cancellation and invalid-argument tests after contract normalization.
3. Add diagnostics snapshot tests for capability detection branches.

Exit criteria:
- each listed file >= 55%

### Priority C: UI and shell surfaces
Current 0% files include XAML and shell bootstrap paths (App/Program/MainWindow/Settings windows).

Plan:
1. Exclude generated and non-code artifacts from coverage denominator:
- obj/**
- *.axaml
- generated regex/import files

2. For code-behind and startup paths:
- add smoke tests where practical (view model integration over full UI automation unless needed).
- avoid chasing raw line coverage in pure UI markup.

Exit criteria:
- coverage metric reflects meaningful executable code, not markup/generated noise.

## Suggested Execution Order
1. Stabilize failing tests (contract alignment).
2. Re-run full coverage baseline.
3. Implement Priority A tests.
4. Re-run coverage and lock improvements.
5. Implement Priority B tests.
6. Re-run coverage and publish summary.
7. Apply Priority C denominator cleanup + light startup tests.

## Coverage Gate Proposal
- Short-term gate (after stabilization): no new/modified production file under 60% unless explicitly approved.
- Medium-term gate: raise core pipeline/provider files to 70%+.
- Regression rule: PR coverage for touched lines must not decrease.

## Recommended First PR Slice
- Fix RegistryTests + provider exception/cancellation expectation alignment.
- Add first test set for ContainerizedTranslationProvider and ContainerizedProviderReadiness.
- Re-run coverage and post delta report.
