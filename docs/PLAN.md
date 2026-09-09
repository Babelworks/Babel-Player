# Docs, Plan, and Status Map

This file is the entry point for repo status and planning docs.

## Current Repo Truth

As of the current codebase:

- the end-to-end dubbing workflow is implemented
- streaming overlap exists for transcription, translation, and TTS
- the managed local GPU host is the default GPU backend
- Docker remains an advanced optional backend using the same service contract
- export exists for `.srt`, `.mp3`, and `.mp4`
- SortFormer CPU ONNX diarization is a selectable alternative to WeSpeaker
- multi-speaker UI pauses at `Diarized` for speaker review; headless `--dub` continues automatically

The main remaining work is hardening, progress UX, and continued architecture cleanup, not missing core pipeline stages.

## Canonical Current Docs

Read in this order for agents and contributors:

1. [AGENTS.md](../AGENTS.md) — repo rules, preferences, testing constraints
2. [AI-CONTEXT.md](AI-CONTEXT.md) — structure, providers, commands, artifacts
3. [architecture.md](architecture.md) — structural boundaries and state ownership
4. [PLAN.md](PLAN.md) — this map
5. [Engineering-Plan.md](Engineering-Plan.md) — maintained engineering status
6. [Next-Priorities-2026-04-16.md](Next-Priorities-2026-04-16.md) — short active follow-up list

## Active Topical Docs

- [testing-requirements.md](testing-requirements.md) — maintained `BabelPlayer.Tests` policy
- [containers.md](containers.md) — containers, WSL, GPU hosting
- [headless-cli.md](headless-cli.md) — `--dub` / `--tui` CLI
- [install-windows-release.md](install-windows-release.md) — Windows release install
- [typography.md](typography.md) — typography tokens
- [cpu-transcription-benchmark.md](cpu-transcription-benchmark.md) — CPU transcription benchmark runbook
- [privacy-policy.md](privacy-policy.md) — privacy policy
- [model-audits/sortformer.md](model-audits/sortformer.md) — SortFormer model audit and follow-ups

## Reference (not adopted / long-form)

- [reference/architecture-reactive-ui-zafiro.md](reference/architecture-reactive-ui-zafiro.md) — ReactiveUI / Zafiro feasibility (not adopted)
- [reference/Avalonia-12-Full-Opportunity-Dossier.md](reference/Avalonia-12-Full-Opportunity-Dossier.md) — Avalonia 12 leverage catalog
- [reference/babel-2.0-tenets.md](reference/babel-2.0-tenets.md) — Babel 2.0 inference discipline
- [reference/babel_player_ui_pitch.md](reference/babel_player_ui_pitch.md) — aspirational UX pitch

## Future / not active

- [plans/cloud/](plans/cloud/) — AWS offload and Azure premium Qwen notes

## History

- [history/README.md](history/README.md) — conventions
- [history/smoke/](history/smoke/) — milestone verification notes
- [history/benchmarks/](history/benchmarks/) — benchmark artifacts and leaderboard
- [history/planning/](history/planning/) — retired plans and hardware analyses
- [history/handoffs/](history/handoffs/) — point-in-time agent handoffs
- [history/conductor/](history/conductor/) — completed conductor plans
- [history/design/](history/design/) — design-system audit / handoff snapshots

Redirect stubs: [smoke/README.md](smoke/README.md), [benchmarks/README.md](benchmarks/README.md).

## Document Intent

- `README.md` — user-facing overview and install/build guidance
- `AGENTS.md` — rules, preferences, workspace facts
- `AI-CONTEXT.md` — current technical ground truth
- `architecture.md` — structural rules and boundaries
- `Engineering-Plan.md` — current engineering status
- dated plans / smoke notes / handoffs — historical snapshots only

If a dated plan conflicts with the code, the code and the canonical current docs above win.
