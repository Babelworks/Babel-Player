# Docs, Plan, and Status Map

This file is the entry point for repo status and planning docs.

## Current Repo Truth

As of the current codebase:

- the end-to-end dubbing workflow is implemented
- streaming overlap exists for transcription, translation, and TTS
- the managed local GPU host is the default GPU backend
- Docker remains an advanced optional backend using the same service contract
- export exists for `.srt`, `.mp3`, and `.mp4`

The main remaining work is hardening, progress UX, and continued architecture cleanup, not missing core pipeline stages.

## Canonical Current Docs

- [AI-CONTEXT.md](AI-CONTEXT.md)
  Repo structure, provider matrix, commands, and artifact paths.
- [architecture.md](architecture.md)
  Structural boundaries and state ownership.
- [architecture-reactive-ui-zafiro.md](architecture-reactive-ui-zafiro.md)
  Draft feasibility study for ReactiveUI / Zafiro in the presentation layer. Not adopted.
- [Engineering-Plan.md](Engineering-Plan.md)
  Maintained engineering status and current implementation summary.
- [Next-Priorities-2026-04-16.md](Next-Priorities-2026-04-16.md)
  Short active follow-up list.
- [sortformer-diarization-plan.md](sortformer-diarization-plan.md)
  Active plan for the SortFormer CPU ONNX diarization port (`sortformer-local`).
- [history/smoke/](history/smoke/)
  Timeline evidence and milestone verification notes.
- [history/benchmarks/](history/benchmarks/)
  Benchmark result artifacts and leaderboard history.

## Retired or Historical Planning Docs

These files are kept for chronology only. Do not use them as current implementation truth:

- [Remaining-Implementation-Plan-2026-04-12.md](Remaining-Implementation-Plan-2026-04-12.md)
- [Milestones-Tracker-2026-04-08.md](Milestones-Tracker-2026-04-08.md)

If a dated plan conflicts with the code, the code and the canonical current docs above win.

## Document Intent

Use each doc for what it is:

- `README.md`
  User-facing project overview and install/build guidance.
- `AGENTS.md`
  Repo rules, learned preferences, and workspace facts.
- `AI-CONTEXT.md`
  Current technical ground truth for contributors and agents.
- `architecture.md`
  Structural rules and boundaries.
- `Engineering-Plan.md`
  Current engineering status.
- dated plans or milestone trackers
  historical snapshots only.
