# History

Canonical current docs live under `docs/PLAN.md`. Everything here is timeline evidence or retired planning material.

## `smoke/`

Milestone smoke notes: manual gate evidence and status (`complete` / `partial` / `failed`). New notes for milestone work should be added here using the naming pattern `milestone-NN-short-label.md` (see [CONTRIBUTING.md](../../CONTRIBUTING.md)).

Status conventions:

- `partial` often means implementation landed but manual/hardware validation remained outstanding at capture time.
- `historical-retired` means the note is preserved for context while the referenced feature/path is no longer active.
- Canonical current implementation state lives in `docs/Engineering-Plan.md`.

## `benchmarks/`

CPU transcription benchmark JSON artifacts and the auto-generated [LEADERBOARD.md](benchmarks/LEADERBOARD.md). New result files go here; `scripts/aggregate_leaderboard.py` refreshes the leaderboard.

## `planning/`

Retired implementation plans, coverage snapshots, marketing briefs, and hardware acceleration analyses.

## `handoffs/`

Point-in-time agent handoff notes. Do not treat as current repo truth.

## `conductor/`

Completed or one-shot conductor delegation plans.

## `design/`

Design-system audit and handoff snapshots (Avalonia UI). Prefer live tokens in `Styles/` and [typography.md](../typography.md) for current guidance.
