> Historical follow-up analysis. This file records a point-in-time design discussion and is not maintained as current repo truth.
> Use `docs/AI-CONTEXT.md`, `docs/Engineering-Plan.md`, and `docs/containers.md` for the current runtime and provider posture.

# My response to Initial Opus Hardware Acceleration AnalysiS 

Couple things, for one XTTS isn't a part of the TTS pipeline at this point, so whatever document or memory youre pulling from that mentions XTTS as implemented needs to be revised. Qwen3 is the only vocal cloning TTS option integrated as of now.

Second and more importantly, you're going off some old "phase 0" type assumptions, specifically "does it avoid a premature provider matrix". for context, most of those rules like that and other ground level architecture guidelines and agent instructions were put in place because I had been getting in a habit of trying to build features way too early, like a provider matrix, and accumulating vast technical debt that necessitated starting from scratch. That is to say, those guidelines and plan.md are largely no longer relevant and should be treated as such. We are very well at the phase where we should take into consideration a provider level matrix. 

That all being said, does that change your recommendations at all?

# OPUS RESPONSE:

Both corrections change the recommendation meaningfully. XTTS being absent removes a class of "export-hard autoregressive TTS" anxiety that was distorting the TTS analysis, and Qwen3-TTS slots into the same question shape but with different specifics. The removed "no provider matrix" constraint is the bigger shift. It moves the correct answer from **"one DML path as universal baseline, optimize later"** to **"commit to a per-vendor-optimal matrix now and size the `RuntimeSelectionService` to own it."** I would not adopt the entire matrix on day one, but the target architecture is different and the phasing should reflect that.

Let me save these corrections to memory and then revise.# What changes

**On XTTS / Qwen3.** My previous writeup hinged the TTS cross-vendor question on XTTS. With Qwen3-TTS as the only integrated voice-cloning TTS, the question shape is the same but the specifics differ. Piper was already cross-vendor (pure ONNX VITS, runs on any ORT execution provider). The open question is now whether **Qwen3-TTS can be exported to ONNX or OpenVINO IR**. I do not have high-confidence knowledge that it can. Qwen3 as a family is mostly LLM-focused; Qwen audio/TTS variants ship as PyTorch + CUDA reference implementations with autoregressive decoders and dynamic shapes, which is a known-hard case for ONNX export. This is a concrete research item to add to the roadmap, not something to hand-wave. If Qwen3-TTS exports cleanly, the matrix closes; if not, non-NVIDIA users get Piper plus cloud TTS for premium voices until we either land an export, pick a different cloning model, or build a CUDA-only "premium voices" tier that honest readiness surfaces truthfully.

**On the provider matrix.** Removing the anti-matrix guardrail changes the target architecture, not the raw research findings. My earlier framing of Option 2 as "triples surface area, violates architecture doc" was the wrong conclusion given current intent. With the matrix earned, Option 2 (per-vendor-optimal backends) becomes the correct **end state**. The argument for Option 1 (universal DirectML baseline) was partly architectural; that half of the argument falls away. What remains of the Option 1 argument is pure engineering-cost phasing: filling the matrix row by row is still cheaper than filling it all at once. So Option 1 becomes "the second row to fill, not the final shape."

# Revised target architecture

Commit to an explicit `(stage, vendor, runtime)` matrix owned by `RuntimeSelectionService`, not a three-valued compute profile with smart routing tucked inside.

## Target matrix shape

| Stage | NVIDIA | AMD dGPU | AMD iGPU | Intel dGPU | Intel iGPU | Intel NPU | CPU |
|---|---|---|---|---|---|---|---|
| ASR (Whisper) | faster-whisper CT2 (current) | ORT+DML Whisper | whisper.cpp Vulkan | OpenVINO Whisper | OpenVINO Whisper | OpenVINO NPU (encoder) | faster-whisper CPU |
| NMT (NLLB-200) | CT2 CUDA (current) | ORT+DML NLLB | ORT+DML NLLB | OpenVINO NLLB | OpenVINO NLLB | not suitable | CT2 CPU |
| TTS (Piper) | Piper (any EP) | Piper (DML) | Piper (DML) | Piper (OpenVINO) | Piper (OpenVINO) | n/a | Piper CPU |
| TTS (Qwen3 voice clone) | Qwen3 CUDA (current) | **open** | **open** | **open** | **open** | no | Qwen3 CPU (if supported) |

"Open" cells are the honest uncertainty. Resolve them by investigating Qwen3-TTS ONNX/OpenVINO export viability before committing. Do not list them as "coming soon" in the UI until investigation earns that claim.

## What the matrix implies for code shape

`ComputeProfile` can stay three-valued. The matrix is orthogonal to it. `ComputeProfile` answers "what tier of resource did the user pick" and the matrix answers "which concrete provider is correct for that tier on this hardware." That keeps persisted settings simple and the matrix evolvable without migrations.

`ProviderNames` grows. Prefer flat constants (`WhisperOnnxDml`, `WhisperOpenVinoIntel`, `Nllb200OnnxDml`, `Nllb200OpenVino`, `PiperDml`, `PiperOpenVino`, and so on) over a composite ID scheme. Flat is easier to serialize, easier to grep, and easier for the architecture linter rule about "no magic provider strings outside ProviderNames.cs" to keep catching mistakes.

`HardwareSnapshot` gets first-class vendor classification. Today it only detects NVIDIA via `nvidia-smi`, OpenVINO via Python import, and NPU via CPU name heuristic. The matrix needs: DXGI adapter enumeration (vendor, model, VRAM for AMD/Intel), ORT DML availability probe, explicit Intel Arc vs Xe-iGPU classification (Arc has its own distinct characteristics), AMD RDNA generation detection (some DML-via-AMD paths behave differently on Vega vs RDNA3), and NPU capability with generation/TOPS. Without this, the matrix lookup will operate on incomplete data and the readiness claims will be dishonest.

`RuntimeSelectionService` becomes a real object, not a thin dispatcher. It owns the matrix, consumes `HardwareSnapshot` and user preferences, and returns a `(ProviderId, Reason)` tuple per stage. The reason is important because it feeds the "why did it pick this" diagnostic surface the project already values. User pinning overrides auto selection, and the override reason must be preserved through session persistence so restored sessions do not silently re-route.

`ManagedVenvHostManager` likely splits into per-backend hosts or gains per-backend venv awareness: one CUDA venv (current), one DirectML venv, one OpenVINO venv. whisper.cpp Vulkan is a native binary and does not need a venv. The long-lived inference server direction from M12 becomes especially valuable here because cold-starting multiple Python environments per request would be catastrophic; one warm server per active backend per session is the sane shape.

# Revised phasing

The matrix unlocks the target. Engineering capacity still constrains what ships when. A sensible fill order, top rows first:

**Phase A (foundation).** Expand `HardwareSnapshot` with proper vendor/adapter/capability detection. Stand up `RuntimeSelectionService` as a real service with the matrix wired but mostly CUDA+CPU entries populated. No user-facing change yet. Extract hardware/runtime logic out of the coordinator per the project-instruction guidance.

**Phase B (cross-vendor baseline via DirectML).** Fill the DML column: Whisper-ONNX on DML, NLLB-ONNX on DML, Piper on DML (trivial). This gives every non-NVIDIA user a real GPU path. Ship with smoke evidence on at least one Radeon and one Arc machine.

**Phase C (Intel optimization).** Fill the OpenVINO column for Whisper and NLLB. `HardwareSnapshot.HasOpenVino` is already detected, so this is additive. The matrix prefers OpenVINO over DML when vendor is Intel. Add NPU encoder routing for Lunar Lake as an opportunistic optimization inside the OpenVINO path.

**Phase D (AMD iGPU optimization).** Add whisper.cpp Vulkan as an ASR backend specifically for AMD APU iGPUs where DML perf is weakest. The matrix prefers Vulkan over DML when vendor is AMD and GPU is iGPU-class. This is genuinely optional if DML perf turns out acceptable on 780M/890M.

**Phase E (Qwen3 cross-vendor decision).** Dedicated spike: can Qwen3-TTS be exported to ONNX/OpenVINO with acceptable quality and latency? Outcomes: yes (fill the matrix, TTS is cross-vendor), partial (some quantization regression, accept as optional premium), no (voice cloning remains NVIDIA-only, surface that honestly in the UI, possibly add Chatterbox-ONNX or another cloning model as a cross-vendor alternative).

**Phase F (deferred).** ROCm native on Windows, Ryzen AI NPU for TTS decoders, IPEX-XPU shipping, WebGPU inference. Revisit only if Phase B-E leaves specific unacceptable gaps.

# What I would actively push back on

Having the matrix available does not mean filling every cell is the right investment. Specifically:

**Do not pay the OpenVINO integration cost before Phase B ships.** OpenVINO is an optimization of the DML baseline on Intel hardware. If you try to layer it in parallel with the baseline, you triple the test surface for the same release and have no ground truth to compare the Intel-native perf against. Ship DML universally, benchmark, then decide whether the OpenVINO win is worth the maintenance on a per-stage basis.

**Do not build a plugin system.** The matrix does not need hot-swappable provider registration or a DI container that discovers providers by reflection. A compile-time matrix (enum keys, registered concrete providers, explicit switch in the selection service) is the correct level of abstraction for a solo-dev codebase with a defined set of vendors. Provider matrix does not mean plugin architecture. Keep that distinction sharp.

**Do not let the matrix leak into the coordinator.** `SessionWorkflowCoordinator` still owns workflow state and should ask the selection service for a provider ID per stage, not enumerate backends itself. This is the single-state-owner rule still binding. The matrix is a concern for `RuntimeSelectionService` and the registries.

**Resolve Qwen3 exportability before over-promising on TTS.** Write it down as a Phase B-or-C-parallel research task. If the answer is "no clean export," that is load-bearing for product messaging. "Voice cloning on NVIDIA only, Piper everywhere" is a defensible product statement. "Voice cloning coming soon on all hardware" is fake readiness.

# Sources
Memory entries saved:

- [TTS pipeline composition](file:///C:/Users/ander/AppData/Roaming/Claude/local-agent-mode-sessions/f8f7d88b-0ab7-4faf-ae29-80027804155f/871a46e3-8fff-4528-b042-7a80de2cda0c/spaces/41770203-19b1-4403-acca-ee4aaa84177a/memory/project_tts_pipeline.md)
- [Provider matrix guardrail lifted](file:///C:/Users/ander/AppData/Roaming/Claude/local-agent-mode-sessions/f8f7d88b-0ab7-4faf-ae29-80027804155f/871a46e3-8fff-4528-b042-7a80de2cda0c/spaces/41770203-19b1-4403-acca-ee4aaa84177a/memory/feedback_provider_matrix_now_allowed.md)
