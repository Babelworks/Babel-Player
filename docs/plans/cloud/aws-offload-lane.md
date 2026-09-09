# AWS Offload Lane (Phase 3)

Source: AWS Solutions Architect follow-up, 2026-09-07 (TrackDub + AWS technical resources).

## Prescribed stack

- **AWS Batch**: managed GPU job execution (provision, run stages, teardown, auto-retry).
- **EC2 G5 (A10G) vs G6 (L4)** benchmark for price/performance. Both CUDA + TensorRT;
  ONNX Runtime runs unmodified at tier-1 acceleration speeds.
- **Spot instances** (70-90% savings); dubbing is interruptible and Batch retries — ideal fit.
- **SageMaker Inference Recommender** for later cross-instance benchmarking
  (latency, throughput, cost per inference).
- **Cost guardrails**: AWS Budgets with alerts at 50% and 80% of monthly target —
  set up before any runs.

## Pending

- **Bedrock model access** (escalation case #178778897900076) — maps to LLM-adjacent
  stages (text refinement, glossary, translation QA). Define concrete workloads so
  approval lands against real usage.

## Implications for this repo

- The AWS framing is ONNX-native (TrackDub core), not the torch host. Babel-side AWS
  work, if any, is the containerized inference host (existing Dockerfile) as a Batch
  job with S3 artifact round-trip — or deferred to TrackDub core entirely.
- The headless CLI (`--dub`) is the natural container entry point for batch jobs.
- The session/artifact model (durable, resumable stages) already matches Batch's
  job semantics.

## Open items

- [ ] G5 vs G6 benchmark matrix (note: cloud EPs are standard TensorRT/CUDA, not TensorRT-RTX)
- [ ] Batch job granularity (per-stage jobs vs single pipeline job)
- [ ] S3 artifact layout mirroring the session format
- [ ] Cost-per-minute-dubbed model
