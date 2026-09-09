# SortFormer diarization model audit

Landed as selectable CPU ONNX diarization (`sortformer-local`). WeSpeaker remains the default when multi-speaker detection is enabled.

## Model

| Field | Value |
|---|---|
| Provider id | `sortformer-local` |
| Catalog model id | `sortformer-4spk` |
| Hub repo | `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` |
| Mirror | `tonythethompson/diar-streaming-sortformer-4spk-v2.1-onnx` |
| Hub file | `diar_streaming_sortformer_4spk-v2.1.onnx` |
| Install path | `{SortFormerModelDir}/onnx/model.onnx` |
| Default dir | `%LOCALAPPDATA%/BabelPlayer/models/sortformer-4spk` |
| Approx size | ~492 MB |
| Pinned SHA-256 | `82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00` |
| Runtime | CPU ONNX (`Microsoft.ML.OnnxRuntime`) |
| Max speakers | 4 |

## License

- NVIDIA Open Model License
- Attribution required in `THIRD_PARTY_LICENSES.md`
- Source card: https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx

## Integration notes

- Download verifies SHA-256 before readiness succeeds; concurrent downloads serialize per install directory.
- Engine port follows TrackDub `Trackdub.Inference.Onnx.SortFormer` (mel/FFT contract must stay aligned; MathNet.Numerics `FourierOptions.Matlab`).
- Mel features are extracted for the full recording before chunked ONNX inference (NeMo-style offline features + streaming encoder/spkcache/fifo).
- Speaker labels from the engine (`spk_0`) are normalized to Babel `spk_00` in `SortFormerDiarizationProvider`.
- SortFormer audio (including `.wav`) is ffmpeg-normalized to 16 kHz PCM16 mono before decode.
- Multi-speaker UI pauses at `Diarized` for speaker review; headless `--dub` continues automatically.

## Engine layout

`Services/SortFormer/`:

- `SortFormerModelCatalog.cs`
- `SortFormerModelFiles.cs`
- `SortFormerFeatureExtractor.cs`
- `SortFormerDiarizationEngine.cs`
- `SortFormerDiarizationProvider.cs`

## Follow-ups

- **Bounded feature streaming:** `SortFormerDiarizationEngine.RunStreamingFeatureModel` extracts mel features for the entire decoded recording before chunked ONNX steps. Long media can spike heap while samples and full feature buffers are both live. A later hardening pass should stream or window feature extraction so peak memory stays bounded, without changing the AOSC/spkcache/fifo contract.
