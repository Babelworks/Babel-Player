# Next priorities (April 2026)

Short owner intent list for upcoming engineering work. For overall project status, see `Engineering-Plan.md`.

---

## 1. Gordon refactor — Tier 2

**Context:** Tier 1 delivered the inference execution boundary (`IInferenceExecutionEngine`), stage orchestrators, pipeline state machine, and related coordinator slim-down. Tier 2 is the **next tranche** of Gordon’s refactor plan (execution/architecture follow-on).

**Note on naming:** This is **not** the same as the older “Tier 2 = EmbeddedPlayback ViewModel decomposition” item in `history/planning/Remaining-Implementation-Plan-2026-04-12.md` — that VM work is already done (`Engineering-Plan.md` Phase 6.1b).

**Action:** Implement Tier 2 per the maintained Gordon plan (whatever Tier 2 is in that plan: e.g. deeper seams, cancellation, telemetry, or further coordinator decomposition — follow the plan doc, do not rename tiers here).

---

## 2. Real pipeline progress bars

**Goal:** Move the main pipeline UI from **mostly indeterminate** progress to **meaningful fractional progress** wherever providers can supply it.

**Direction:**

- Propagate **concrete 0–1 (or phase-weighted)** progress from long-running stages (transcription, translation, TTS, diarization, downloads) through the existing **`IProgress<double>`** / **`PipelineStageContext`** path.
- Keep updates **UI-thread friendly** (throttle/debounce if a subprocess emits high-frequency events).
- **`EmbeddedPlaybackPipelineViewModel`** already exposes `PipelineProgressPercent`, `IsPipelineProgressIndeterminate`, and copy via `PipelineProgressStatusLine`; **`MainWindow.axaml`** already binds a **`ProgressBar`**. The work is **plumbing honest values** into those bindings, not inventing a new control.

**Success criteria:** During a normal run, users see the bar **advance** during at least the dominant long stages, with a clear policy when only indeterminate progress is possible (e.g. unknown-length work).

---

## Tracking

When either item ships, update `Engineering-Plan.md` (or a milestone smoke note under `docs/history/smoke/`) so this file does not become stale authority.
