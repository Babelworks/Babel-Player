Short answer: yes, WinML (the new 2025-era one, shipped through Windows App SDK 1.8+) is the right primary target for non-NVIDIA acceleration, and it collapses a meaningful chunk of what I was proposing to hand-build. But it does not solve the two hard-locks in Babel Player's current pipeline, and the Python deployment path has real rough edges. The architectural shape I sketched stays; the implementation underneath gets simpler.



\### What "Windows ML" actually is now



This is easy to get wrong because "WinML" has referred to three different things over the years. The current one, the one Microsoft is actively investing in, is:



\- A redistributable ML layer that ships with the Windows App SDK (1.8+) and is installed per machine, not per app. Multiple apps share one copy of ONNX Runtime.

\- A dynamic execution-provider catalog (`ExecutionProviderCatalog.EnsureAndRegisterCertifiedAsync`) that downloads and registers vendor EPs on demand rather than requiring your installer to ship them. As of early 2026 the certified set covers: VitisAI and MIGraphX (AMD), OpenVINO (Intel), NvTensorRtRtx (NVIDIA), QNN (Qualcomm), plus built-in CPU and DirectML.

\- An automatic EP selection policy knob (`SessionOptions.SetEpSelectionPolicy(MAX\_PERFORMANCE | PREFER\_NPU | MAX\_EFFICIENCY | DEFAULT)`) that picks a concrete EP at session-creation time given the hardware it finds.

\- A migration story from standalone `onnxruntime` / `onnxruntime-directml` to a WinML-provided ORT. DirectML itself is in sustained engineering; new optimization work is moving to vendor-native EPs behind WinML.

\- C#, C++/WinRT, C, and Python surfaces. Python uses `wasdk-Microsoft.Windows.AI.MachineLearning\[all]` plus `onnxruntime-windowsml`, and requires a non-Store Python interpreter (Store-packaged Python cannot load the bootstrapped runtime).



The mechanism that matters for us: the vendor-dispatch layer I was proposing to build in `RuntimeSelectionService` is something Microsoft has already built and maintains. If a model is in ONNX form and the op set is supported by a vendor EP, WinML can route the session onto AMD / Intel / NVIDIA / Qualcomm silicon without Babel Player owning per-vendor install logic.



\### How this maps onto the three stages



ASR (faster-whisper / CTranslate2). CTranslate2 has no WinML or ONNX Runtime backend; it ships its own CUDA/CPU kernels. WinML does nothing for our current ASR path. The only way WinML helps ASR is by replacing CT2 with an ONNX-based whisper (either Microsoft-published Olive-optimized ONNX whisper, whisper.cpp-ONNX, or a community Whisper.onnx) and letting the EP policy pick the backend. That is a real provider substitution, not a configuration change, and it is the same "replace CT2 for non-NVIDIA" work I flagged in the earlier option set. WinML does not dissolve that; it just makes the non-NVIDIA side of the swap cleaner.



NMT (NLLB-200 on CT2). Same story, and worse because NLLB-200 is less trivially available as a performant quantized ONNX. Transformers.js / Optimum-ORT / Hugging Face have NLLB ONNX exports but the quality-vs-size tradeoffs have to be validated. The CT2 lock is real until someone ships an acceptable ONNX replacement.



TTS. Piper is already ONNX and CPU-friendly, so WinML can run it with trivially small gain over today. The interesting one is Qwen3-TTS. Two open questions govern whether WinML helps at all for voice cloning: (1) does a usable ONNX or OpenVINO export of Qwen3-TTS exist or can we produce one, and (2) do the ops land inside the coverage set of the AMD/Intel/NVIDIA EPs for this model size. Neither of those is a WinML question, they are model-export questions, and they are the same blocker I noted earlier. WinML lets us skip the "which runtime on which vendor" plumbing once the export exists.



Net: WinML improves the NMT-replacement and the Qwen3 deployment story the moment those exports exist. It does not make the exports exist.



\### Deployment constraints to price in



\- Windows 11 24H2 (build 26100) or higher for the vendor-optimized EPs. Windows 10 users and pre-24H2 Windows 11 users get CPU and DirectML, not VitisAI / OpenVINO / TensorRT RTX. That is a real customer-segment cut.

\- Windows App SDK bootstrap has to land before the Python process can import the WinML-ORT. In our subprocess model this means the spawner, not the subprocess, has to validate the bootstrap, and the long-lived inference server we are already moving toward is the natural place to do it once.

\- The Python path's Microsoft Store-Python exclusion is a footgun. The long-lived server should pin the Python it launches against.

\- EP download is transient and network-dependent the first time. For an offline-first dubbing workstation we likely want an explicit "preflight EP acquisition" step rather than first-session download in the middle of user work.



\### What this does and does not change in the plan



It changes: Phase B (hand-integrating a DirectML Python venv with its own EP management) and Phase C (standing up a separate OpenVINO provider path) collapse into a single WinML-backed inference host. We stop owning per-vendor runtime selection at the ORT level. We still own session policy ("user forced NPU", "user forced AMD dGPU") as UX, but we implement it by setting `SetEpSelectionPolicy` or explicit EP registration, not by picking runtimes ourselves.



It does not change: the matrix shape. We still have a vendor × stage × backend mapping, because CT2 is CUDA-only for two of three stages and that is a model-runtime fact, not a WinML fact. `RuntimeSelectionService` still needs to exist; it just has fewer custom branches because one of those branches is "hand it to WinML and let the EP policy dispatch". The "no fake readiness" rule still applies, including for WinML-backed stages, so smoke evidence per vendor is still the bar.



It also does not change: the biggest uncertainty is Qwen3-TTS exportability, and the second biggest is NLLB-on-ONNX quality parity with the current CT2 builds. Both of these have to be actually tried. Neither WinML nor any competing stack improves them by existing.



\### Revised recommendation



Target WinML as the non-CUDA runtime, owned by a long-lived Python inference server that the coordinator talks to. Route Piper and any future ONNX whisper / ONNX NLLB / ONNX Qwen3-TTS through it. Keep the existing faster-whisper-CT2 and CTranslate2-NMT paths as the "NVIDIA + CUDA" high-performance branch, because they are genuinely faster than ORT-on-TensorRT-RTX for these specific models today and we lose nothing by keeping them. `RuntimeSelectionService` becomes: pick `{CT2-CUDA | WinML}` per stage, with WinML's internal EP policy handling AMD/Intel/NPU dispatch under the hood. Detect Windows build and EP availability in `HardwareSnapshot` so selection has real inputs rather than the nvidia-smi-only view it has today. Treat exportable Qwen3-TTS and exportable NLLB-200-ONNX as blocking spikes for the non-NVIDIA branch and do those spikes before committing the phase.



The cross-vendor story goes from "build a small provider matrix" to "build a two-runtime dispatcher where one of the runtimes is WinML and Microsoft maintains the vendor fan-out". That is meaningfully less code owned by us and more alignment with where Windows AI deployment is actually heading.

