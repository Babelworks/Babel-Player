## Contributor Rules

### Start Here

Read these before non-trivial work:

- [AGENTS.md](AGENTS.md)
- [AGENTS.md](AGENTS.md)
- [docs/AI-CONTEXT.md](docs/AI-CONTEXT.md)
- [docs/architecture.md](docs/architecture.md)
- [docs/PLAN.md](docs/PLAN.md)
- [docs/Engineering-Plan.md](docs/Engineering-Plan.md)
- [docs/testing-requirements.md](docs/testing-requirements.md)

Use `docs/PLAN.md` as the document map. Dated plan and milestone files are historical unless that file says otherwise.

### Worktrees

Use the root helper from the repo checkout when you want an isolated branch workspace (PowerShell 7+; `pwsh` — the script uses `#Requires -Version 7.0`):

```powershell
.\worktree.ps1 new codex/feature-name
.\worktree.ps1 sync codex/feature-name
.\worktree.ps1 remove codex/feature-name -DeleteBranch
.\worktree.ps1 list
```

By default, the helper derives the worktree folder name from the current checkout directory as `<repo-folder>.wt` (override with `BABEL_PLAYER_WORKTREE_ROOT`) and rebases feature branches onto `origin/main` (override with `-BaseRef`).
`.\worktree.ps1 sync` aborts if the target worktree has uncommitted changes, so commit, stash, or discard those changes before running it.

### Project Posture

The repo is built around one real user outcome:

```text
load media -> transcript -> translated dialogue -> spoken output -> preview/refine -> resume later
```

Contributions should strengthen that path.

Do not drift the project into shell-first polish, framework-first cleanup, or speculative architecture work that does not help the workflow above.

### Scope Discipline

Do:

- keep changes tightly scoped
- prefer real vertical slices over speculative extension points
- fix the blocker in front of you, not every adjacent imperfection
- preserve truthful runtime behavior and readiness states

Do not:

- mix unrelated refactors into milestone or bug-fix work
- add fake surfaces or silent fallbacks
- document or present incomplete work as shipped
- start downstream feature work just because the shape seems obvious

### Truthful Behavior Only

If something is incomplete, the UI and docs should say so plainly.

That includes:

- no fake buttons
- no pretend-ready provider paths
- no hidden fallback behavior that changes compute path or provider choice without making it obvious
- no stale status docs claiming work is still open after the code has moved on

### Verification Requirements

Baseline verification before opening a PR:

```powershell
dotnet build Babel-Player.sln -c Release
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
python scripts/check-architecture.py
```

Also run this when you touched smoke-covered seams:

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "Category=Smoke"
```

If the change affects UI or workflow behavior, include a short manual verification note.

### Testing Policy

`BabelPlayer.Tests` is the maintained suite.

Keep it:

- deterministic
- fast enough for routine local and CI use
- free of real Python, ffmpeg, container, libmpv, network, manual, or performance-dependent dependencies

Before adding or modifying tests, read [docs/testing-requirements.md](docs/testing-requirements.md).

### Smoke Notes and Historical Evidence

Milestone smoke notes live under `docs/history/smoke/`.

Rules:

- use milestone-based filenames
- keep status honest
- treat them as timeline evidence, not current status authority

Current status belongs in:

- `docs/Engineering-Plan.md`
- `docs/Next-Priorities-2026-04-16.md`

### Refactors

Refactors are justified when they:

- unblock current work
- reduce real complexity in the path being changed
- remove a proven source of instability

They are not justified by:

- architectural purity
- future-proofing alone
- a desire to make the entire repo uniform mid-change

### Review Expectations

A good PR is:

- narrow
- honest about what it completes
- verified
- accompanied by smoke or manual notes when relevant

A bad PR:

- mixes unrelated cleanup with feature work
- expands scope after the core task is already solved
- introduces stale or contradictory docs

### Definition of a Good Contribution

A good contribution leaves the repo more truthful, more verifiable, and more usable for the actual dubbing workflow.

## License Agreement

Babel Player is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)).
By contributing, you agree that your contributions will be licensed under the same
terms. First-time contributors are asked to sign the contributor license agreement
at https://cla.fiveonefour.com/sign/trackdubllc before a PR can be merged.