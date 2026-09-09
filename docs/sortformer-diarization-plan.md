# SortFormer Diarization Port

Active implementation plan for `feature/sortformer-diarization`.

## Goal

Land NVIDIA streaming SortFormer as a selectable CPU ONNX diarization provider (`sortformer-local`), following the Chatterbox playbook. WeSpeaker stays the default when diarization is on. No GPU/TRT in this branch.

## Locked decisions

- Provider id: `sortformer-local` (`ProviderNames.SortFormerLocal`)
- CPU ONNX only (`Microsoft.ML.OnnxRuntime` 1.29.0)
- Diarization stays off by default. Enabling the checkbox still selects WeSpeaker unless the user picks SortFormer.
- Hard cap: 4 speakers.
- No new Python. Do not retire WeSpeaker in this PR.
- Model: pin SHA-256 `82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00` (~492 MB). Install as `{modelRoot}/onnx/model.onnx`. License: NVIDIA Open Model License, attribution required.
- Port from TrackDub `Trackdub.Inference.Onnx.SortFormer`. Drop planner, session pool, TRT factory.
- Normalize TrackDub `spk_0` to Babel `spk_00` in the provider.

## Architecture

```text
SourceMedia -> FfmpegExtractFullAudio (16 kHz mono)
           -> SortFormerDiarizationProvider
           -> SortFormerDiarizationEngine
           -> FeatureExtractor (mel 128)
           -> ONNX chunk/spkcache/fifo
           -> DiarizedSegment spk_00..spk_03
           -> MergeDiarizationIntoTranscript
```

## Engine layout

`Services/SortFormer/`:

- `SortFormerModelCatalog.cs`
- `SortFormerModelFiles.cs`
- `SortFormerFeatureExtractor.cs`
- `SortFormerDiarizationEngine.cs`
- `SortFormerDiarizationProvider.cs`

FFT/mel must not drift from TrackDub (MathNet.Numerics 5.0.0, `FourierOptions.Matlab`).

## Related UX

Multi-speaker UI pauses after diarization for speaker wizard review, then Continue runs translation and dub. Headless `--dub` does not pause (auto-continues). See `docs/history/smoke/milestone-13-diarized-pause-and-gpu-warmup.md` for the historical partial pause intent now restored on `feature/diarized-speaker-review-pause`.

## Verification

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
.\bin\Dev\net10.0\BabelPlayer.exe --dub --media <clip> --diarization sortformer-local --no-mp4
```

## Out of scope

- Flipping the default away from WeSpeaker
- Deleting WeSpeaker
- TensorRT / CUDA / 8-speaker variants

## Follow-ups

- **Bounded feature streaming:** `SortFormerDiarizationEngine.RunStreamingFeatureModel` currently extracts mel features for the entire decoded recording (NeMo-style) before chunked ONNX steps. That matches the TrackDub/NeMo offline-feature + streaming-encoder pattern, but long media can spike heap while samples and full feature buffers are both live. A later hardening pass should stream or window feature extraction so peak memory stays bounded, without changing the AOSC/spkcache/fifo contract.
