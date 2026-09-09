# SortFormer diarization model audit

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

- Download verifies SHA-256 before readiness succeeds.
- Engine port follows TrackDub `Trackdub.Inference.Onnx.SortFormer` (mel/FFT contract must stay aligned).
- Mel features are extracted for the full recording before chunked ONNX inference (NeMo-style). Follow-up: bounded feature streaming for long media; see `docs/sortformer-diarization-plan.md` Follow-ups.
- Speaker labels from the engine (`spk_0`) are normalized to Babel `spk_00` in `SortFormerDiarizationProvider`.
- WeSpeaker remains the default when multi-speaker detection is enabled in the UI.
