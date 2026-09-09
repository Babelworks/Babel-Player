using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Babel.Player.Services.SortFormer;

public sealed record SortFormerSpeakerTurn(
    string SpeakerKey,
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    bool HasOverlap);

public sealed class SortFormerDiarizationEngine : IDisposable
{
    public const int MaxSupportedSpeakers = 4;
    public const int TargetSampleRate = 16000;

    private const float SpeakerActiveThreshold = 0.5f;
    private const float OverlapThreshold = 0.5f;
    private const int StreamingChunkModelFrames = 124;
    private const int StreamingRightContextModelFrames = 1;
    private const int StreamingFeatureSubsampling = 8;
    private const int StreamingFifoFrames = 124;
    private const int StreamingSpeakerCacheFrames = 188;
    private const int StreamingEmbeddingDimension = 512;
    private const int StreamingChunkStrideFeatureFrames = StreamingChunkModelFrames * StreamingFeatureSubsampling;
    private const int StreamingFeedFeatureFrames =
        (StreamingChunkModelFrames + StreamingRightContextModelFrames) * StreamingFeatureSubsampling;

    private static readonly SortFormerFeatureExtractor FeatureExtractor = new();
    private readonly InferenceSession _session;
    private int _disposed;

    public SortFormerDiarizationEngine(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("SortFormer ONNX model not found.", modelPath);

        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Environment.ProcessorCount,
        };
        _session = new InferenceSession(modelPath, options);
        EnsureModelSpeakerCapacity(_session, SortFormerModelCatalog.ModelId);
    }

    public IReadOnlyList<SortFormerSpeakerTurn> Diarize(
        float[] samples,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(samples);
        cancellationToken.ThrowIfCancellationRequested();

        // Do not hard-mask diarization with VAD regions; VAD misses would permanently erase speech.
        if (UsesStreamingFeatureInputs(_session))
        {
            Tensor<float> probabilityTensor = RunStreamingFeatureModel(_session, samples, cancellationToken);
            return DecodeTurns(probabilityTensor, durationSeconds, SortFormerModelCatalog.ModelId);
        }

        using var inputSet = CreateInputSet(_session, samples);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputSet.Values);
        Tensor<float> waveformProbs = ResolveProbabilityTensor(outputs);
        return DecodeTurns(waveformProbs, durationSeconds, SortFormerModelCatalog.ModelId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _session.Dispose();
    }

    private static bool UsesStreamingFeatureInputs(InferenceSession session) =>
        session.InputMetadata.ContainsKey("chunk") &&
        session.InputMetadata.ContainsKey("spkcache") &&
        session.InputMetadata.ContainsKey("fifo");

    private static DenseTensor<float> RunStreamingFeatureModel(
        InferenceSession session,
        float[] samples,
        CancellationToken cancellationToken)
    {
        using SortFormerFeatureInputSet features = FeatureExtractor.Extract(samples);
        if (features.FrameCount <= 0)
        {
            return new DenseTensor<float>(Array.Empty<float>(), [0, MaxSupportedSpeakers]);
        }

        var state = new SortFormerStreamingState();
        var predictionData = new List<float>();
        int speakerCount = MaxSupportedSpeakers;
        int chunkCount = CeilingDivide(features.FrameCount, StreamingChunkStrideFeatureFrames);

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int startFrame = chunkIndex * StreamingChunkStrideFeatureFrames;
            int currentFeatureFrameCount = Math.Min(
                StreamingFeedFeatureFrames,
                features.FrameCount - startFrame);

            using var inputSet = CreateStreamingFeatureInputSet(
                session.InputMetadata,
                features,
                startFrame,
                currentFeatureFrameCount,
                state);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(inputSet.Values);

            Tensor<float> rawPredictions = ResolveRequiredFloatTensor(outputs, "spkcache_fifo_chunk_preds");
            Tensor<float> rawEmbeddings = ResolveRequiredFloatTensor(outputs, "chunk_pre_encode_embs");
            int outputSpeakerCount = ResolveFeatureCount(rawPredictions);
            if (outputSpeakerCount > MaxSupportedSpeakers)
            {
                throw new InvalidOperationException(
                    $"SortFormer ONNX export produced {outputSpeakerCount} speakers, exceeding the maximum supported count of {MaxSupportedSpeakers}.");
            }

            if (predictionData.Count == 0)
            {
                speakerCount = outputSpeakerCount;
            }
            else if (speakerCount != outputSpeakerCount)
            {
                throw new InvalidOperationException("SortFormer ONNX export changed speaker count across streaming chunks.");
            }

            int validModelFrameCount = CeilingDivide(currentFeatureFrameCount, StreamingFeatureSubsampling);
            int keepModelFrameCount = Math.Min(StreamingChunkModelFrames, validModelFrameCount);
            int predictionStartFrame = state.SpeakerCacheFrameCount + state.FifoFrameCount;
            float[] chunkPredictions = ExtractTensorFrameSlice(
                rawPredictions,
                predictionStartFrame,
                keepModelFrameCount,
                speakerCount);
            predictionData.AddRange(chunkPredictions);

            float[] chunkEmbeddings = ExtractTensorFrameSlice(
                rawEmbeddings,
                0,
                keepModelFrameCount,
                StreamingEmbeddingDimension);

            state.Update(chunkEmbeddings, keepModelFrameCount, validModelFrameCount);
        }

        int frameCount = predictionData.Count / speakerCount;
        return new DenseTensor<float>(predictionData.ToArray(), [frameCount, speakerCount]);
    }

    private static InputSet CreateInputSet(InferenceSession session, float[] samples)
    {
        IReadOnlyDictionary<string, NodeMetadata> inputs = session.InputMetadata;

        KeyValuePair<string, NodeMetadata> waveformInput = default;
        if (inputs.TryGetValue("waveform", out NodeMetadata? waveformMeta))
        {
            waveformInput = new KeyValuePair<string, NodeMetadata>("waveform", waveformMeta);
        }
        else if (inputs.TryGetValue("audio_signal", out NodeMetadata? audioSignalMeta))
        {
            waveformInput = new KeyValuePair<string, NodeMetadata>("audio_signal", audioSignalMeta);
        }
        else
        {
            KeyValuePair<string, NodeMetadata>[] floatInputs = inputs
                .Where(static candidate => candidate.Value.ElementType == typeof(float))
                .ToArray();
            if (floatInputs.Length == 0)
            {
                throw new InvalidOperationException("SortFormer ONNX export does not expose any float waveform input.");
            }

            if (floatInputs.Length > 1)
            {
                throw new InvalidOperationException($"SortFormer ONNX export has {floatInputs.Length} float inputs; expected exactly one waveform input.");
            }

            waveformInput = floatInputs[0];
        }

        if (string.IsNullOrWhiteSpace(waveformInput.Key))
        {
            throw new InvalidOperationException("SortFormer ONNX export does not expose a float waveform input.");
        }

        string waveformInputName = waveformInput.Key;
        int[] waveformDimensions = waveformInput.Value.Dimensions.ToArray();
        IReadOnlyList<NamedOnnxValue> values =
        [
            NamedOnnxValue.CreateFromTensor(
                waveformInputName,
                new DenseTensor<float>(samples, ResolveWaveformShape(waveformDimensions, samples.Length)))
        ];

        KeyValuePair<string, NodeMetadata> lengthInput = default;
        if (inputs.TryGetValue("length", out NodeMetadata? lengthMeta))
        {
            lengthInput = new KeyValuePair<string, NodeMetadata>("length", lengthMeta);
        }
        else if (inputs.TryGetValue("audio_signal_length", out NodeMetadata? audioLengthMeta))
        {
            lengthInput = new KeyValuePair<string, NodeMetadata>("audio_signal_length", audioLengthMeta);
        }
        else
        {
            lengthInput = inputs.FirstOrDefault(static candidate =>
                candidate.Value.ElementType == typeof(long) &&
                candidate.Key.Contains("length", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(lengthInput.Key))
        {
            values = values
                .Append(NamedOnnxValue.CreateFromTensor(
                    lengthInput.Key,
                    new DenseTensor<long>(new long[] { samples.Length }, [1])))
                .ToArray();
        }

        return new InputSet(values);
    }

    private static int[] ResolveWaveformShape(IReadOnlyList<int> modelDimensions, int sampleCount)
    {
        if (modelDimensions.Count == 1)
        {
            return [sampleCount];
        }

        if (modelDimensions.Count == 2)
        {
            return [1, sampleCount];
        }

        throw new InvalidOperationException("SortFormer ONNX export waveform input must be rank 1 or 2.");
    }

    private static InputSet CreateStreamingFeatureInputSet(
        IReadOnlyDictionary<string, NodeMetadata> inputs,
        SortFormerFeatureInputSet features,
        int startFrame,
        int currentFeatureFrameCount,
        SortFormerStreamingState state)
    {
        EnsureRequiredInput(inputs, "chunk", typeof(float));
        EnsureRequiredInput(inputs, "chunk_lengths", typeof(long));
        EnsureRequiredInput(inputs, "spkcache", typeof(float));
        EnsureRequiredInput(inputs, "spkcache_lengths", typeof(long));
        EnsureRequiredInput(inputs, "fifo", typeof(float));
        EnsureRequiredInput(inputs, "fifo_lengths", typeof(long));

        int chunkLength = StreamingFeedFeatureFrames * SortFormerFeatureExtractor.MelBins;
        var chunkData = System.Buffers.ArrayPool<float>.Shared.Rent(chunkLength);
        Array.Clear(chunkData, 0, chunkLength);
        features.CopyFramesTo(chunkData, 0, startFrame, currentFeatureFrameCount);

        IReadOnlyList<NamedOnnxValue> values =
        [
            NamedOnnxValue.CreateFromTensor(
                "chunk",
                new DenseTensor<float>(
                    new Memory<float>(chunkData, 0, chunkLength),
                    [1, StreamingFeedFeatureFrames, SortFormerFeatureExtractor.MelBins])),
            NamedOnnxValue.CreateFromTensor(
                "chunk_lengths",
                new DenseTensor<long>(new long[] { currentFeatureFrameCount }, [1])),
            NamedOnnxValue.CreateFromTensor(
                "spkcache",
                new DenseTensor<float>(
                    state.SpeakerCacheEmbeddings,
                    [1, state.SpeakerCacheFrameCount, StreamingEmbeddingDimension])),
            NamedOnnxValue.CreateFromTensor(
                "spkcache_lengths",
                new DenseTensor<long>(new long[] { state.SpeakerCacheFrameCount }, [1])),
            NamedOnnxValue.CreateFromTensor(
                "fifo",
                new DenseTensor<float>(
                    state.FifoEmbeddings,
                    [1, state.FifoFrameCount, StreamingEmbeddingDimension])),
            NamedOnnxValue.CreateFromTensor(
                "fifo_lengths",
                new DenseTensor<long>(new long[] { state.FifoFrameCount }, [1]))
        ];

        return new InputSet(values, chunkData);
    }

    private static void EnsureRequiredInput(
        IReadOnlyDictionary<string, NodeMetadata> inputs,
        string name,
        Type elementType)
    {
        if (!inputs.TryGetValue(name, out NodeMetadata? metadata))
        {
            throw new InvalidOperationException($"SortFormer ONNX export is missing required input '{name}'.");
        }

        if (metadata.ElementType != elementType)
        {
            throw new InvalidOperationException(
                $"SortFormer ONNX export input '{name}' has element type '{metadata.ElementType.Name}', expected '{elementType.Name}'.");
        }
    }

    private static Tensor<float> ResolveRequiredFloatTensor(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string outputName)
    {
        foreach (DisposableNamedOnnxValue output in outputs)
        {
            if (!string.Equals(output.Name, outputName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                return output.AsTensor<float>();
            }
            catch (InvalidCastException exception)
            {
                throw new InvalidOperationException(
                    $"SortFormer ONNX export output '{outputName}' is not a float tensor.",
                    exception);
            }
        }

        throw new InvalidOperationException($"SortFormer ONNX export did not produce required output '{outputName}'.");
    }

    private static int ResolveFeatureCount(Tensor<float> tensor)
    {
        int[] dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length is not (2 or 3))
        {
            throw new InvalidOperationException("SortFormer ONNX export tensor must be rank 2 or batch-first rank 3.");
        }

        return dimensions[^1];
    }

    private static float[] ExtractTensorFrameSlice(
        Tensor<float> tensor,
        int startFrame,
        int frameCount,
        int featureCount)
    {
        if (frameCount <= 0)
        {
            return [];
        }

        int[] dimensions = tensor.Dimensions.ToArray();
        int availableFrames = dimensions.Length switch
        {
            2 when dimensions[1] == featureCount => dimensions[0],
            3 when dimensions[0] == 1 && dimensions[2] == featureCount => dimensions[1],
            _ => throw new InvalidOperationException(
                $"SortFormer ONNX export tensor shape [{string.Join(", ", dimensions)}] does not match expected feature count {featureCount}.")
        };

        if (startFrame < 0 || startFrame + frameCount > availableFrames)
        {
            throw new InvalidOperationException(
                $"SortFormer ONNX export tensor has {availableFrames} frame(s); requested {frameCount} frame(s) from {startFrame}.");
        }

        var data = new float[frameCount * featureCount];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
            {
                data[(frameIndex * featureCount) + featureIndex] = dimensions.Length == 2
                    ? tensor[startFrame + frameIndex, featureIndex]
                    : tensor[0, startFrame + frameIndex, featureIndex];
            }
        }

        return data;
    }

    private static int CeilingDivide(int value, int divisor) => (value + divisor - 1) / divisor;

    private static float[] ConcatenateFrames(
        float[] first,
        int firstFrameCount,
        float[] second,
        int secondFrameCount,
        int featureCount)
    {
        if (firstFrameCount == 0)
        {
            return second.ToArray();
        }

        if (secondFrameCount == 0)
        {
            return first.ToArray();
        }

        var combined = new float[(firstFrameCount + secondFrameCount) * featureCount];
        Array.Copy(first, 0, combined, 0, firstFrameCount * featureCount);
        Array.Copy(second, 0, combined, firstFrameCount * featureCount, secondFrameCount * featureCount);
        return combined;
    }

    private static float[] SliceFrames(float[] source, int startFrame, int frameCount, int featureCount)
    {
        if (frameCount <= 0)
        {
            return [];
        }

        var sliced = new float[frameCount * featureCount];
        Array.Copy(source, startFrame * featureCount, sliced, 0, frameCount * featureCount);
        return sliced;
    }

    private static float[] AppendAndKeepLastFrames(
        float[] existing,
        int existingFrameCount,
        float[] appended,
        int appendedFrameCount,
        int featureCount,
        int maxFrameCount)
    {
        float[] combined = ConcatenateFrames(existing, existingFrameCount, appended, appendedFrameCount, featureCount);
        int combinedFrameCount = existingFrameCount + appendedFrameCount;
        if (combinedFrameCount <= maxFrameCount)
        {
            return combined;
        }

        return SliceFrames(combined, combinedFrameCount - maxFrameCount, maxFrameCount, featureCount);
    }

    private static Tensor<float> ResolveProbabilityTensor(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        foreach (DisposableNamedOnnxValue output in outputs)
        {
            try
            {
                Tensor<float> tensor = output.AsTensor<float>();
                int[] dimensions = tensor.Dimensions.ToArray();
                if (dimensions.Length is 2 or 3)
                {
                    return tensor;
                }
            }
            catch (InvalidCastException)
            {
            }
        }

        throw new InvalidOperationException("SortFormer ONNX export did not produce a frame probability tensor.");
    }

    private static void EnsureModelSpeakerCapacity(InferenceSession session, string? modelAlias)
    {
        int? speakerDim = ResolveOutputSpeakerDimension(session);
        if (speakerDim is > MaxSupportedSpeakers)
        {
            throw new InvalidOperationException(
                $"SortFormer model '{modelAlias ?? "unknown"}' declares {speakerDim.Value} output speakers, " +
                $"but only {MaxSupportedSpeakers} are supported. Use a 4-speaker SortFormer model export.");
        }
    }

    private static int? ResolveOutputSpeakerDimension(InferenceSession session)
    {
        if (session.OutputMetadata.TryGetValue("spkcache_fifo_chunk_preds", out NodeMetadata? streamingMeta))
        {
            int[] dims = streamingMeta.Dimensions.ToArray();
            if (dims.Length > 0 && dims[^1] > 0)
            {
                return dims[^1];
            }
        }

        foreach (KeyValuePair<string, NodeMetadata> output in session.OutputMetadata)
        {
            if (output.Value.ElementType != typeof(float))
            {
                continue;
            }

            int[] dims = output.Value.Dimensions.ToArray();
            if (dims.Length is 2 or 3 && dims[^1] > 0)
            {
                return dims[^1];
            }
        }

        return null;
    }

    private static IReadOnlyList<SortFormerSpeakerTurn> DecodeTurns(Tensor<float> probabilities, double durationSeconds, string? modelAlias)
    {
        if (durationSeconds <= 0d)
        {
            return [];
        }

        (int frameCount, int speakerCount, Func<int, int, float> accessor) = CreateTensorAccessor(probabilities);
        if (frameCount <= 0 || speakerCount <= 0)
        {
            return [];
        }

        if (speakerCount > MaxSupportedSpeakers)
        {
            throw new InvalidOperationException(
                $"SortFormer model '{modelAlias ?? "unknown"}' produced {speakerCount} speakers, " +
                $"exceeding the maximum supported count of {MaxSupportedSpeakers} (frameCount={frameCount}).");
        }

        double secondsPerFrame = durationSeconds / frameCount;
        if (!double.IsFinite(secondsPerFrame) || secondsPerFrame <= 0d)
        {
            return [];
        }

        var turns = new List<SortFormerSpeakerTurn>();
        ActiveTurn? activeTurn = null;

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int primarySpeakerIndex = -1;
            float primarySpeakerProbability = float.NegativeInfinity;
            int activeSpeakerCount = 0;

            for (int speakerIndex = 0; speakerIndex < speakerCount; speakerIndex++)
            {
                float probability = accessor(frameIndex, speakerIndex);
                if (probability >= OverlapThreshold)
                {
                    activeSpeakerCount++;
                }

                if (probability > primarySpeakerProbability)
                {
                    primarySpeakerProbability = probability;
                    primarySpeakerIndex = speakerIndex;
                }
            }

            double frameStart = frameIndex * secondsPerFrame;
            double frameEnd = Math.Min(durationSeconds, (frameIndex + 1) * secondsPerFrame);
            bool isSilentFrame = primarySpeakerProbability < SpeakerActiveThreshold || primarySpeakerIndex < 0;
            if (isSilentFrame)
            {
                FlushActiveTurn(turns, ref activeTurn, durationSeconds);
                continue;
            }

            bool hasOverlap = activeSpeakerCount > 1;
            if (activeTurn is not null &&
                activeTurn.SpeakerIndex == primarySpeakerIndex &&
                Math.Abs(activeTurn.EndSeconds - frameStart) <= secondsPerFrame * 1.5d)
            {
                activeTurn = activeTurn with
                {
                    EndSeconds = frameEnd,
                    ConfidenceSum = activeTurn.ConfidenceSum + primarySpeakerProbability,
                    FrameCount = activeTurn.FrameCount + 1,
                    HasOverlap = activeTurn.HasOverlap || hasOverlap
                };
                continue;
            }

            FlushActiveTurn(turns, ref activeTurn, durationSeconds);
            activeTurn = new ActiveTurn(
                primarySpeakerIndex,
                frameStart,
                frameEnd,
                primarySpeakerProbability,
                FrameCount: 1,
                hasOverlap);
        }

        FlushActiveTurn(turns, ref activeTurn, durationSeconds);
        return turns;
    }

    private static (int FrameCount, int SpeakerCount, Func<int, int, float> Accessor) CreateTensorAccessor(Tensor<float> tensor)
    {
        int[] dimensions = tensor.Dimensions.ToArray();
        return dimensions.Length switch
        {
            2 => (dimensions[0], dimensions[1], (frameIndex, speakerIndex) => tensor[frameIndex, speakerIndex]),
            3 when dimensions[0] == 1 => (dimensions[1], dimensions[2], (frameIndex, speakerIndex) => tensor[0, frameIndex, speakerIndex]),
            3 when dimensions[2] == 1 => (dimensions[0], dimensions[1], (frameIndex, speakerIndex) => tensor[frameIndex, speakerIndex, 0]),
            _ => throw new InvalidOperationException("SortFormer ONNX export probability tensor must be rank 2 or batch-first rank 3.")
        };
    }

    private static void FlushActiveTurn(
        ICollection<SortFormerSpeakerTurn> turns,
        ref ActiveTurn? activeTurn,
        double durationSeconds)
    {
        if (activeTurn is null)
        {
            return;
        }

        double clippedStart = Math.Clamp(activeTurn.StartSeconds, 0d, durationSeconds);
        double clippedEnd = Math.Clamp(activeTurn.EndSeconds, clippedStart, durationSeconds);
        if (clippedEnd > clippedStart)
        {
            turns.Add(new SortFormerSpeakerTurn(
                $"spk_{activeTurn.SpeakerIndex}",
                clippedStart,
                clippedEnd,
                activeTurn.ConfidenceSum / activeTurn.FrameCount,
                activeTurn.HasOverlap));
        }

        activeTurn = null;
    }

    private sealed class InputSet(IReadOnlyList<NamedOnnxValue> values, float[]? rentedArray = null)
        : IDisposable
    {
        public IReadOnlyList<NamedOnnxValue> Values { get; } = values;

        public void Dispose()
        {
            foreach (IDisposable value in Values.OfType<IDisposable>())
            {
                value.Dispose();
            }

            if (rentedArray is not null)
            {
                ArrayPool<float>.Shared.Return(rentedArray);
            }
        }
    }

    private sealed class SortFormerStreamingState
    {
        public float[] SpeakerCacheEmbeddings { get; private set; } = [];

        public int SpeakerCacheFrameCount { get; private set; }

        public float[] FifoEmbeddings { get; private set; } = [];

        public int FifoFrameCount { get; private set; }

        public void Update(
            float[] chunkEmbeddings,
            int chunkEmbeddingFrameCount,
            int validChunkFrameCount)
        {
            int previousFifoFrameCount = FifoFrameCount;
            float[] combinedFifoEmbeddings = ConcatenateFrames(
                FifoEmbeddings,
                FifoFrameCount,
                chunkEmbeddings,
                chunkEmbeddingFrameCount,
                StreamingEmbeddingDimension);
            int combinedFifoFrameCount = previousFifoFrameCount + chunkEmbeddingFrameCount;

            if (combinedFifoFrameCount <= StreamingFifoFrames)
            {
                FifoEmbeddings = combinedFifoEmbeddings;
                FifoFrameCount = combinedFifoFrameCount;
                return;
            }

            int popOutFrameCount = Math.Max(
                StreamingChunkModelFrames,
                validChunkFrameCount - StreamingFifoFrames + previousFifoFrameCount);
            popOutFrameCount = Math.Min(popOutFrameCount, combinedFifoFrameCount);

            float[] popOutEmbeddings = SliceFrames(
                combinedFifoEmbeddings,
                0,
                popOutFrameCount,
                StreamingEmbeddingDimension);

            SpeakerCacheEmbeddings = AppendAndKeepLastFrames(
                SpeakerCacheEmbeddings,
                SpeakerCacheFrameCount,
                popOutEmbeddings,
                popOutFrameCount,
                StreamingEmbeddingDimension,
                StreamingSpeakerCacheFrames);
            SpeakerCacheFrameCount = Math.Min(
                StreamingSpeakerCacheFrames,
                SpeakerCacheFrameCount + popOutFrameCount);

            int remainingFifoFrameCount = combinedFifoFrameCount - popOutFrameCount;
            FifoEmbeddings = SliceFrames(
                combinedFifoEmbeddings,
                popOutFrameCount,
                remainingFifoFrameCount,
                StreamingEmbeddingDimension);
            FifoFrameCount = remainingFifoFrameCount;
        }
    }

    private sealed record ActiveTurn(
        int SpeakerIndex,
        double StartSeconds,
        double EndSeconds,
        double ConfidenceSum,
        int FrameCount,
        bool HasOverlap);
}
