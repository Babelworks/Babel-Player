# Third-Party Licenses

Babel Player incorporates third-party software and pre-trained models. Their licenses are listed below.

---

## Bundled Native Binaries

### libmpv
- **Version:** 2026-09-03 (git f5bcfb1954), built by [zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild)
- **License:** GPL-2.0-or-later
- **Source:** https://github.com/mpv-player/mpv
- **License text:** https://github.com/mpv-player/mpv/blob/master/LICENSE.GPL

### ffmpeg
- **Version:** 2026-03-30 (git e54e117998)
- **x64 build:** [GyanD/codexffmpeg](https://github.com/GyanD/codexffmpeg) — GPL build
- **ARM64 build:** [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) — LGPL build
- **License:** LGPL-2.1-or-later / GPL-2.0-or-later (build-dependent)
- **Source:** https://ffmpeg.org/
- **License text:** https://ffmpeg.org/legal.html

### uv
- **Version:** latest (fetched at first run from [astral-sh/uv](https://github.com/astral-sh/uv))
- **License:** MIT or Apache-2.0
- **Source:** https://github.com/astral-sh/uv
- **License text:** https://github.com/astral-sh/uv/blob/main/LICENSE-MIT

### Chatterbox multilingual voice cloning (ONNX)
- **Version:** revision 452d3f434aa592098f1eedac9099f33642ab2da5
- **Source:** [onnx-community/chatterbox-multilingual-ONNX](https://huggingface.co/onnx-community/chatterbox-multilingual-ONNX)
- **License:** MIT
- **Note:** voice cloning requires explicit user consent (Settings or explicit headless opt-in); cloning never runs by default.

---

## .NET / NuGet Packages

All packages below are licensed under the MIT License unless otherwise noted.

| Package | Version | License | Source |
|---|---|---|---|
| Avalonia | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Themes.Fluent | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| AvaloniaUI.DiagnosticsSupport | 2.2.0-beta3 | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Tmds.DBus.Protocol | 0.92.0 | MIT | https://github.com/tmds/Tmds.DBus |
| System.Security.Cryptography.ProtectedData | 10.0.5 | MIT | https://github.com/dotnet/runtime |
| MathNet.Numerics | 5.0.0 | MIT | https://github.com/mathnet/mathnet-numerics |

---

## Python Packages

### Inference Runtime (all paths)

| Package | Version | License | Source |
|---|---|---|---|
| fastapi | 0.104.0 – 0.115.4 | MIT | https://github.com/tiangolo/fastapi |
| uvicorn | 0.24.0 – 0.30.6 | BSD-3-Clause | https://github.com/encode/uvicorn |
| pydantic | 2.10.6 | MIT | https://github.com/pydantic/pydantic |
| python-multipart | 0.0.9 – 0.0.22 | Apache-2.0 | https://github.com/andrew-d/python-multipart |
| faster-whisper | 1.2.1 | MIT | https://github.com/SYSTRAN/faster-whisper |
| ctranslate2 | 4.7.1 | MIT | https://github.com/OpenNMT/CTranslate2 |
| sentencepiece | 0.2.0 – 0.2.1 | Apache-2.0 | https://github.com/google/sentencepiece |
| transformers | 4.46.3 – 5.0.0 | Apache-2.0 | https://github.com/huggingface/transformers |
| accelerate | 1.12.0 | Apache-2.0 | https://github.com/huggingface/accelerate |
| torch | 2.5.1 – 2.8.0 | BSD-3-Clause | https://github.com/pytorch/pytorch |
| torchaudio | 2.5.1 – 2.8.0 | BSD-2-Clause | https://github.com/pytorch/audio |
| torchvision | 0.23.0 | BSD-3-Clause | https://github.com/pytorch/vision |
| edge-tts | 7.2.8 | MIT | https://github.com/rany2/edge-tts |
| soundfile | 0.12.1 | BSD-3-Clause | https://github.com/bastibe/python-soundfile |
| numpy | 1.24.3 – 1.26.4 | BSD-3-Clause | https://github.com/numpy/numpy |
| requests | 2.32.3 – 2.33.0 | Apache-2.0 | https://github.com/psf/requests |
| tts (Coqui) | 0.22.0 | MPL-2.0 | https://github.com/coqui-ai/TTS |
| pydub | 0.25.1 | MIT | https://github.com/jiaaro/pydub |
| safetensors | 0.6.2 | Apache-2.0 | https://github.com/huggingface/safetensors |
| scipy | 1.14.1 | BSD-3-Clause | https://github.com/scipy/scipy |

### CPU Runtime (additional)

| Package | Version | License | Source |
|---|---|---|---|
| openai-whisper | 20240930 | MIT | https://github.com/openai/whisper |
| onnxruntime | 1.19.2 | MIT | https://github.com/microsoft/onnxruntime |
| silero-vad | 5.1.0 | AGPL-3.0 | https://github.com/snakers4/silero-vad |
| peft | 0.13.2 | Apache-2.0 | https://github.com/huggingface/peft |
| scikit-learn | 1.3.2 | BSD-3-Clause | https://github.com/scikit-learn/scikit-learn |
| s3prl | 0.4.17 | MIT | https://github.com/s3prl/s3prl |

### GPU Runtime (additional)

| Package | Version | License | Source |
|---|---|---|---|
| nemo-toolkit | 2.7.2 | Apache-2.0 | https://github.com/NVIDIA/NeMo |
| qwen-tts | ≥0.1.1 | Apache-2.0 | https://github.com/QwenLM/Qwen |
| hf_xet | ≥0.1.0 | Apache-2.0 | https://github.com/huggingface/xet-core |

### Git Dependencies

| Package | Commit | License | Source |
|---|---|---|---|
| wespeaker | c92349a | Apache-2.0 | https://github.com/wenet-e2e/wespeaker |

---

## Pre-trained Models

> **Important:** Several models used by Babel Player carry non-commercial license restrictions.
> See the notice below before using this software commercially.

### NLLB-200 (Meta)
- **Models:** `nllb-200-distilled-600M`, `nllb-200-distilled-1.3B`, `nllb-200-1.3B`
- **License:** CC-BY-NC-4.0 — **non-commercial use only**
- **Source:** https://huggingface.co/facebook/nllb-200-distilled-600M
- **License text:** https://creativecommons.org/licenses/by-nc/4.0/

### Qwen3-TTS (Alibaba)
- **License:** Apache-2.0
- **Source:** https://huggingface.co/Qwen/Qwen3-TTS
- **License text:** https://www.apache.org/licenses/LICENSE-2.0

### Faster-Whisper / Whisper (OpenAI)
- **License:** MIT
- **Source:** https://huggingface.co/Systran
- **License text:** https://github.com/openai/whisper/blob/main/LICENSE

### Piper TTS voices
- **License:** varies by voice model (most are MIT or CC0); see individual model cards on HuggingFace
- **Source:** https://github.com/rhasspy/piper

### WeSpeaker speaker embeddings
- **License:** Apache-2.0
- **Source:** https://github.com/wenet-e2e/wespeaker

### SortFormer streaming diarization (ONNX)
- **License:** NVIDIA Open Model License
- **Source:** https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx
- **Pinned SHA-256:** `82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00`
- **Notes:** CPU ONNX 4-speaker streaming SortFormer (`sortformer-local`). Attribution required under the NVIDIA Open Model License.

### NeMo (NVIDIA) diarization models
- **License:** Apache-2.0 (toolkit); individual model weights may vary
- **Source:** https://github.com/NVIDIA/NeMo

---

## Non-Commercial Use Notice

The following pre-trained models are licensed for **non-commercial use only**:

- **NLLB-200** (Meta) — CC-BY-NC-4.0

If you intend to use Babel Player in a commercial context, you must replace these models with commercially-licensed alternatives or obtain a separate commercial license from the model authors.
