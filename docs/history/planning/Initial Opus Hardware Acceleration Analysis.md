> Historical analysis transcript. This file captures an earlier hardware-acceleration discussion and is not a maintained source of current architecture or provider truth.
> Use `docs/AI-CONTEXT.md`, `docs/Engineering-Plan.md`, and `docs/containers.md` for the current repo state.

I'll dispatch parallel research agents and pull the architecture context simultaneously so I can synthesize against the actual constraints.Now dispatching the three research agents in parallel plus pulling the architecture docs.Let me pull a few more grounding files before synthesis - specifically the M12 plan and the hardware routing files.# Direct answer

You do not have "CUDA plus a port." You have a CUDA-locked pipeline at two of three stages: `FasterWhisper` (ASR) and the `CTranslate2`/`Nllb200` translation path are both CT2, and CT2 has no official AMD or Intel GPU backend (only community ROCm forks, nothing on Windows, no Intel GPU at all). The only stage that is already vendor-portable is `Piper` TTS (plain ONNX). That means "add AMD/Intel GPU support" is not an incremental port. It forces one of two strategic moves: (a) ship a **parallel runtime** for non-NVIDIA users, or (b) **replace CTranslate2** with a runtime that has broad execution-provider coverage. The single most economically sane move is to adopt **ONNX Runtime with DirectML as a second managed GPU host** and route AMD, Intel dGPU, and both vendors' iGPUs through it. Everything else is either a narrower slice of that or a more expensive fork.

# The landscape as it actually exists today

## What the three research agents converged on

**AMD on Windows.** ROCm/HIP SDK native on Windows is real but PyTorch-ROCm wheels for Windows are still effectively WSL2-only in 2025/early 2026, which makes it unshippable in a desktop installer. MIOpen coverage on Windows lags, which matters because convolution-heavy models need it. MIGraphX on Windows is thinner than ORT+DML. Ryzen AI / XDNA NPU is still research-grade for non-demo workloads. The only **production-grade AMD path on Windows** is **ONNX Runtime + DirectML**. Vulkan via `whisper.cpp` is the surprise strong path for ASR on both Radeon iGPUs (780M/890M) and dGPUs, and it ships as a tiny C++ binary which fits the project's existing subprocess model.

**Intel on Windows.** OpenVINO is first-class, Windows-native, and already partially detected by `HardwareSnapshot.DetectOpenVino`. It covers Whisper and NLLB well, NPU routing (Meteor/Lunar Lake) for quantized Whisper encoders, and can target Arc dGPU and Xe iGPU. OpenVINO's **weak spot is TTS**: XTTS and Chatterbox have no first-party OpenVINO recipe, Piper works because it is ONNX. IPEX-XPU is heavy and WSL-biased, so shipping it in the installer is painful. DirectML on Arc is real but runs 20-50% slower than OpenVINO on the same model.

**Cross-vendor reality.** The research grounded in real sources (whisper.cpp/onnxruntime/huggingface/optimum-amd/optimum-intel docs and issues) confirmed:

- CTranslate2 is effectively CUDA-only for GPU. No Intel GPU path exists even via oneDNN. AMD ROCm forks exist only for specific Instinct/MI class silicon on Linux. [CTranslate2 hardware support](https://opennmt.net/CTranslate2/hardware_support.html)
- `onnx-community/chatterbox-ONNX` and a multilingual variant exist and run on DirectML today. This is the most promising XTTS-class, cross-vendor TTS option.
- Piper is pure ONNX VITS and runs on any ORT execution provider with zero work.
- whisper.cpp Vulkan achieves ~12x over CPU on Ryzen 6800H-class iGPUs per [Phoronix Whisper.cpp 1.8.3 Vulkan 12x](https://www.phoronix.com/news/Whisper-cpp-1.8.3-12x-Perf), and is mature on NVIDIA/AMD/Intel.
- llama.cpp NLLB support is an open PR ([#18359](https://github.com/ggml-org/llama.cpp/pull/18359)), not shippable yet.
- "Optimum" is a shared Python API, not a shared runtime. Optimum-Intel is OpenVINO. Optimum-AMD is ROCm + Ryzen AI. Optimum-ORT with DML is the only subset that is actually cross-vendor.

# Options, ranked against Babel Player's architecture

Each option is judged against the five constraints that matter here: (1) does it respect `SessionWorkflowCoordinator` as the single state owner, (2) does it avoid a premature provider matrix, (3) does it keep the Python/C# boundary explicit, (4) does it actually work cross-vendor on Windows for the three workloads, (5) can it honestly claim readiness.

## Option 1 (recommended baseline): ORT+DirectML universal GPU host, introduced stepwise

A new "managed GPU-DirectML host" parallel to the existing NVIDIA venv. One long-lived Python process using `onnxruntime-directml` plus `optimum-ort`. ASR runs Whisper-ONNX; TTS runs Piper today and can add `chatterbox-ONNX` later; NMT runs NLLB-ONNX (distilled 600M) exported via Optimum. `RuntimeSelectionService` (new, per project instructions) consumes `HardwareSnapshot` and a user preference and returns a provider ID per stage. `ComputeProfile` stays three-valued; vendor dispatch happens inside the `RuntimeSelectionService`, not in the enum.

**Coverage**: AMD Radeon (RDNA2/3/4), AMD APU iGPUs (Vega/RDNA2/3 including 780M/890M), Intel Arc (A770/A750/B580), Intel Xe iGPU (Meteor/Lunar Lake), plus any DX12-capable NPU that exposes a DML backend. Also works as a universal fallback on NVIDIA.

**Perf**: ~1.5-3x slower than CUDA on NVIDIA, but 3-6x faster than CPU on AMD/Intel iGPU, similar to vendor-native on dGPU for the models we actually use.

**Cost**: One new managed venv bootstrap path, one new inference server endpoint per workload, three model conversions (Whisper already done upstream; NLLB via Optimum; Chatterbox-ONNX already published). Detect-and-route work in `HardwareSnapshot` + new `RuntimeSelectionService`.

**Honest readiness bar**: A workload is "DirectML-ready" only after smoke evidence of an actual Radeon or Arc machine running the artifact and producing a valid dub. Nothing less counts.

**Strengths**: One runtime to maintain for non-NVIDIA users. Matches the project bias against provider matrices. Reuses the subprocess model. Optimizes toward the M12 "long-lived inference server" direction, since ORT benefits enormously from keeping the EP warm.

**Weaknesses**: XTTS-v2 has no clean ONNX export, so if XTTS-specific voices matter, this path needs to accept Chatterbox-ONNX as the replacement or keep XTTS CUDA-only. DirectML perf on NVIDIA is worse than CUDA, so NVIDIA users stay on the current CT2 path.

## Option 2: DirectML for AMD, OpenVINO for Intel, CUDA for NVIDIA (three parallel vendor paths)

Best absolute performance per vendor. Intel users get OpenVINO's Whisper encoder acceleration, NPU routing on Lunar Lake, and near-native NLLB perf. AMD users get DML. NVIDIA users keep CT2.

**Cost**: Three Python environments, three provider implementations per stage (the provider matrix the architecture doc explicitly warns against). Roughly 2x the integration surface of Option 1.

**When it makes sense**: Only if Option 1 ships and benchmarks show the DML-on-Intel penalty is unacceptable. Treat OpenVINO as a future optimization on top of the DML baseline, not as a first move. That way you earn the second runtime with evidence instead of asserting it.

## Option 3: whisper.cpp-Vulkan for ASR, ORT+DML for NMT and TTS

Heterogeneous but each component is mature. whisper.cpp Vulkan is smaller than ORT and ~12x over CPU on modern iGPUs, which is genuinely strong for the ASR stage (the heaviest wall-clock component on short dubs). It ships as a single binary, no Python, no venv. TTS and NMT stay on ORT+DML.

**Strengths**: Lowest install footprint for ASR. Works uniformly on every GPU including mobile iGPUs where DML is weak.
**Weaknesses**: Two C++/Python runtimes to manage. More host bootstrap work. Diarization (WeSpeaker) is already Python-only, so you still need a Python path anyway.

**Verdict**: Worth prototyping as an ASR fast-path after Option 1 proves out, not as a first move.

## Option 4: Replace CTranslate2 entirely with ORT/Optimum

The cleanest end state. CT2 goes away. Faster-whisper becomes WhisperX or ORT-Whisper. NLLB-CT2 becomes NLLB-ONNX. Everything is one runtime with EP selection. Arguably the right long-term direction.

**Cost**: Retires a production-hardened fast path on NVIDIA. CT2 is genuinely very fast on CUDA; replacing it will cost NVIDIA users real performance unless you keep CT2 behind a "high-performance NVIDIA" flag. Big migration, high risk, low short-term user benefit.

**Verdict**: Revisit after Option 1 has been in production for two releases and the team has real multi-vendor benchmark data. Do not do this before M13.

## Option 5: Intel-only iGPU/NPU exploit via OpenVINO (narrow, cheap, evidence-earning)

`HardwareSnapshot` already detects OpenVINO presence and the Intel Core Ultra NPU label. Add exactly one thing: an OpenVINO-backed Whisper provider that runs on Intel iGPU or NPU. Nothing else changes. No AMD work. Most of the architecture stays put.

**Strengths**: Smallest possible slice. Uses infrastructure that already exists in the repo. Gives Lunar Lake / Meteor Lake users a meaningful speedup.
**Weaknesses**: Does nothing for AMD users. Does not solve the broader cross-vendor question.

**Verdict**: A useful spike, but not a strategic answer. Treat as a side quest.

# What I would actually do, staged

**Stage 0 (architectural groundwork, small).** Extract a `RuntimeSelectionService` as the project instructions already call for. Expand `HardwareSnapshot` with three new probes: DirectML support (via ORT Python import or `Microsoft.AI.DirectML.dll` presence), a lightweight DX12 adapter enumeration (DXGI, you already have a DXGI path in `QueryActiveHdrDisplay`), and an AMD/Intel GPU name classifier. No behavior change yet; the new service just returns the same CUDA/CPU decisions it always did.

**Stage 1 (first non-NVIDIA GPU path).** Add a `ManagedDirectMlHostManager` parallel to `ManagedVenvHostManager`. Its venv holds `onnxruntime-directml` + `optimum[onnxruntime]` + model weights. Implement **one** provider first: `Whisper_Dml` ASR. Smoke it on a Radeon machine and an Intel Arc machine. This is the honest-readiness gate; no "it should work" claims.

**Stage 2 (cover the other two stages).** Add `Nllb200_Dml` (NLLB-ONNX via Optimum export, running under the same host) and `Chatterbox_Dml` (using `onnx-community/chatterbox-ONNX`). Piper already runs on any EP with a single-line swap. TTS gets two offerings cross-vendor: Piper (fast, decent quality) and Chatterbox (slower, XTTS-class quality).

**Stage 3 (Intel-specific polish, optional).** Add OpenVINO as a second EP inside the same host for users whose `HardwareSnapshot.HasOpenVino` is true and whose GPU vendor is Intel. Ranking: OpenVINO > DirectML on Intel, DirectML elsewhere. NPU routing stays opportunistic and only for the Whisper encoder.

**Stage 4 (deferred, possibly never).** Native ROCm on Windows, Ryzen AI NPU, IPEX-XPU, WebGPU. Revisit only if user demand or perf gaps force it.

# Concrete things to be wary of

The XTTS question is load-bearing and under-analyzed. If the product commits to Qwen-TTS or XTTS-v2-specific voices, cross-vendor TTS is harder than cross-vendor ASR or NMT. Before investing in Option 1, decide explicitly whether Chatterbox-ONNX is an acceptable stand-in. If yes, the whole plan works. If no, TTS stays CUDA-only for premium voices and AMD/Intel users get Piper or cloud. State that honestly in the UI.

The perf claims in the AMD and Intel agent reports were training-data-only (both agents flagged this). Before committing to any option, run a real benchmark on one Arc and one Radeon machine with the exact faster-whisper model, the exact NLLB variant, and the exact Chatterbox build the product ships. The DML-vs-CUDA multiplier varies materially by model and by driver version. The cross-vendor agent's numbers are solid because they came from real issues/docs; the vendor-specific numbers are soft.

`HardwareSnapshot` today classifies non-NVIDIA systems as "GPU unknown" (`GpuLine` returns dash unless `nvidia-smi` answers). Before introducing `RuntimeSelectionService`, fix that: enumerate DXGI adapters, record vendor and VRAM, and record DirectML availability. Otherwise the selection service will make decisions on missing data and you will have shipped "fake readiness" for non-NVIDIA hardware.

The coordinator size limit (1300 lines) plus the current constructor bloat (18 parameters, per the retired plan) means this work must not flow into `SessionWorkflowCoordinator` directly. Everything hardware- or runtime-related lands in the new `RuntimeSelectionService` and the new `ManagedDirectMlHostManager`. The coordinator consumes their outputs; it does not own them.

A useful heuristic for committee pressure: if someone asks "why not add OpenVINO first, it has a Windows installer and HardwareSnapshot already detects it" push back that OpenVINO's TTS coverage is weak and it only helps Intel users. DirectML first gives AMD and Intel coverage from one runtime; OpenVINO second is a pure optimization for Intel. Doing the weaker-coverage runtime first inverts the ROI.

# Answering the "what is the trap" question

The trap is treating this as "add an AMD backend" and "add an Intel backend" as two independent tickets. Done that way, you end up with three Python environments, three provider implementations per stage, a factory pattern in the coordinator, and a readiness claim that is true on exactly one developer's machine. The architecture doc explicitly warns against that shape. Treating it as a **single "universal GPU via ORT+DML" ticket with opportunistic vendor-native optimizations later** keeps the abstraction count honest, keeps the provider matrix small, and lets each vendor path be introduced with real evidence.Sources referenced in the research:

- [ONNX Runtime DirectML EP](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [CTranslate2 hardware support](https://opennmt.net/CTranslate2/hardware_support.html)
- [whisper.cpp 1.8.3 Vulkan 12x (Phoronix)](https://www.phoronix.com/news/Whisper-cpp-1.8.3-12x-Perf)
- [onnx-community/chatterbox-ONNX](https://huggingface.co/onnx-community/chatterbox-ONNX)
- [Optimum-Intel (OpenVINO)](https://github.com/huggingface/optimum-intel)
- [Optimum-AMD (ROCm + Ryzen AI)](https://github.com/huggingface/optimum-amd)
- [llama.cpp NLLB PR #18359](https://github.com/ggml-org/llama.cpp/pull/18359)
- [AMD GPUOpen ONNX+DirectML guide](https://gpuopen.com/learn/onnx-directlml-execution-provider-guide-part1/)
- [F5-TTS on AMD via DirectML (Level1Techs)](https://forum.level1techs.com/t/zero-shot-voice-cloning-on-an-amd-gpu-f5-tts-onnx-and-directml-on-windows/248432)
