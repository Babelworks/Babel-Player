# Plan: Reduce UI Verbosity and Over-explanation (COMPLETED)

## Objective
Simplify user-facing status messages, remove redundant instructions, and hide internal technical details from the main workflow UI to provide a more polished and less "wordy" user experience.

## Changes Implemented

### 1. ViewModels
#### MainWindowViewModel.cs
- **Simplified:** The managed backend warmup notice.
- **New Text:** "The local inference host may take 30-60 seconds to start. Please wait for the status to show 'Ready' before running the pipeline."

### 2. Services
#### SessionWorkflowCoordinator.Containerized.cs
- **Simplified:** Removed `AppendWarmupExpectationHint`.
- **Action:** No longer appends "Typical first warm-up..." to the inline status text.

#### SessionWorkflowCoordinator.Pipeline.cs
- **Simplified:** "Vocal and ambiance stems prepared for transcription." -> "Audio prepared for transcription."
- **Simplified:** "Translated {count} segments to {lang}. Ready for TTS/dubbing." -> "Translation complete. Ready for dubbing."

#### SessionWorkflowCoordinator.Orchestrators.Diarization.cs & Playback.cs
- **Simplified:** "Speaker mapping complete..." -> "Speaker analysis complete."

#### SessionWorkflowCoordinator.cs
- **Simplified:** All "Resumed session with..." and "Restored..." messages replaced with a concise "Ready."
- **Simplified:** "Media loaded. Ready for transcription." -> "Media loaded."
- **Simplified:** Pipeline reset messages changed from "Pipeline reset to..." to "Reset to [Stage]."

#### BootstrapDiagnostics.cs & HardwareSnapshot.cs
- **Simplified:** "AVX (no AVX2 — reduced inference performance)" -> "AVX (Legacy)"
- **Simplified:** "none detected (inference will be significantly slower)" -> "No AVX detected"

## Verification
- Verified by architecture linter.
- Manual audit of string literals in modified files.
- Logic preserved; only string content updated.