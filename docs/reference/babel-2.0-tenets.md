# Babel 2.0 Tenets

Disciplinary rules stolen from TrackDub (verified against `Trackdub-Monorepo-Archive`
and the current Trackdub workspace), adapted for Babel Player. These exist so future
agents and contributors don't re-litigate settled decisions — and so the failure
modes that killed Babel Player 1.x (unpinned Python runtimes, silent stalls,
aspirational readiness) cannot return.

Status markers: **[Adopted]** is already true in this repo; **[Pending]** is committed
direction for 2.0 work.

---

## 1. No Python at runtime

TrackDub verified clean: zero C#→Python process spawns, no `requirements.txt`, no venvs.
Every `.py` file in that repo is offline build/CI/model tooling (ONNX exports, manifest
audits, hash verification, Hub mirroring).

- **[Pending]** Babel 2.0 adds no new Python runtime dependencies. Each provider
  migration (transcription, translation, TTS, diarization) must remove a Python
  dependency, never add one.
- Python remains allowed in `scripts/` strictly for offline tooling: model conversion,
  audit helpers, hash evidence. Nothing under `scripts/` may execute during app runs.
- Rationale: the September 2026 transformers 4.x→5.x outage (auto-rebuilt venv,
  `HubertModel` removal, every provider red overnight) is what this rule prevents
  structurally rather than by vigilance.

## 2. Commercial-safe-only; there is no safe mode

TrackDub evolution (commit `b59013eb`, May 2026): deleted the `CommercialSafeMode`
runtime flag everywhere. The product ships only commercial-safe models; safety is
decided at manifest-authoring time, not by a user toggle.

- **[Pending]** Babel 2.0 carries no commercial-safety mode, flag, or setting.
- Model admission is governed by manifest fields: `commercial_allowed` vs
  `commercial_use_verified` (allowed is necessary but NOT sufficient for selection),
  `lane`, `voice_cloning`, `requires_user_consent`, `requires_attribution`.
- `unknown` license means unsafe for every lane until reviewed. A repository license
  is not enough: check code license, pretrained weights, dependency models,
  model-card terms, and known training-data restrictions.
- `commercial_use_verified: true` requires BOTH license confidence AND a non-empty
  artifact SHA-256.
- Explicitly non-commercial routes (TrackDub's Demucs precedent) may live in
  dev/research tooling only — never in the bundled manifest, never selectable
  by any shipped pipeline path.

## 3. Model audits with evidence, including rejections

TrackDub practice: `model-audits/` — one record per model family with auditor, date,
license stack, artifact hashes with pinned revisions, smoke results, and the decision
(flip or remain-blocked). Rejections are recorded too (`musetalk-blocked.md`).

- **[Pending]** Every model Babel 2.0 ships or evaluates gets an audit record under
  `docs/model-audits/` following the same shape: license evidence, pinned revision,
  hash evidence, smoke result, decision.
- Rejected models get a `-blocked.md` record with the reason, so nobody re-evaluates
  them from scratch.
- Hash-evidence files live with the audit; a `verify-manifest-hashes`-style script
  re-checks them in CI.

## 4. Readiness is never one signal

TrackDub rule: plugin resolved → binaries present → registered → device visible →
model downloaded → smoke-tested → stage ran. Registration alone is not readiness,
and a fallback must be reported as what it actually is (DirectML, not "GPU ready").

- **[Adopted]** Provider diagnostics already separate registration from readiness;
  keep it that way. This extends `architecture.md` ("truthful failure states over
  silent fallback") into the inference layer.
- **[Pending]** No stage may report success (or "Ready") without having produced a
  verified artifact. No silent provider substitution: if the engine falls back,
  the UI names the fallback.

## 5. Install-time downloads only, never session-time

TrackDub policy: Model Manager install (after license acceptance) and the CLI
install path may download. Inference session bootstrap, readiness probes, doctor
checks, and benchmarks **never** download.

- **[Pending]** Babel 2.0 download surfaces: explicit model/voice/provider install
  UI (with license acceptance where required) plus a CLI install command. Pipeline
  runs, probes, and the `--dub` driver assume artifacts present and fail loudly
  with install instructions when they are not.
- This is the durable form of the Phase 0 lesson: the 1.x bootstrap assembled the
  world at first-use time. 2.0 assembles nothing at runtime.

## 6. Manifest-driven, hash-pinned artifacts

TrackDub pattern (`bundled-models.manifest.json`, `trt-rtx-ep.manifest.json`):
per-model entries with task, engine family, capabilities, tier, license fields,
pinned revision, SHA-256 per file, per-RID URLs. Fetch scripts verify checksum
and size before use; a manifest-update script refreshes pins deliberately.

- **[Pending]** Native binaries, EP plugin bundles, model weights, and voices all
  resolve through pinned manifests with hash verification. Version bumps are
  deliberate commits (with E2E verification for major bumps), never silent
  floating tags — except where upstream offers no pin, in which case the unpinned
  source is documented as a known risk with an owner.
- **[Adopted]** Precedent in-repo: `fetch-win-native-deps.ps1` pins releases;
  `THIRD_PARTY_LICENSES.md` tracks versions (seed of the full notices file below).

## 7. Third-party notices as a first-class file

TrackDub: `THIRD_PARTY_NOTICES.md` (+ per-component files), attribution-required
models surfaced in export/project metadata, per-vendor license families with
one-time per-machine acceptance persisted in settings.

- **[Adopted]** `THIRD_PARTY_LICENSES.md` exists; grow it toward the full notices
  file as vendored EPs and converted models land.
- **[Pending]** Vendor EP/plugin bundles and converted model families require
  explicit user acceptance before install (persisted flag, viewable license).
  Voice-cloning models additionally require per-session subject consent —
  mandatory and non-bypassable (pairs with the Speaker Reference Wizard).

## 8. Architecture tests enforce dependency direction

TrackDub practice: a dedicated test project asserting project-reference rules
(e.g., worker references Infrastructure/SDK, never Api) plus CI check scripts.

- **[Adopted]** Precedent in-repo: `scripts/check-architecture.py` runs in CI.
- **[Pending]** Add compiled architecture tests for 2.0 boundaries as they form,
  especially: UI layer must not assemble EP/provider policy; inference code must
  not live in shell projects; model execution must be proven in a harness before
  the pipeline depends on it.

## 9. Provider changes follow the gating checklist

TrackDub's gate before any probe-order or allow-list change: catalog provider id
confirmed; registration path implemented and tested; per-stage smoke on
representative hardware; manifest tokens updated; smoke failure falls through
instead of sticking on a false "ready".

- **[Pending]** No new execution provider, model family, or provider-order change
  lands in Babel 2.0 without walking this checklist, evidenced in the model's
  audit record. In particular: adding the TensorRT-RTX route requires the plugin
  bundle manifest, hardware gate, readiness probe, and a smoke pass on NVIDIA
  hardware — not just the NuGet package.

## 10. Retired 1.x patterns

For the record, the 1.x patterns these tenets replace:

- `uv pip install` on user machines (replaced by tenets 5, 6)
- `CommercialSafeMode`-style toggles, if ever proposed (replaced by tenet 2)
- Single-signal readiness ("DLL present = GPU ready") (replaced by tenet 4)
- Unpinned floating dependencies in the inference path (replaced by tenet 6;
  see the transformers 5.x incident)
- Session-time downloads and silent fallbacks (replaced by tenets 4, 5)

---

*Sources: Trackdub-Monorepo-Archive (`docs/adr/ADR-0002`, `docs/internal/tensorrt-rtx-ep-abi-plugin.md`,
`docs/legal/MODEL_LICENSE_POLICY.md`, `docs/internal/model-audits/`, commit `b59013eb`),
current Trackdub workspace (`Trackdub.Inference[.Onnx]`, `Trackdub.Architecture.Tests`).
Verified September 2026.*
