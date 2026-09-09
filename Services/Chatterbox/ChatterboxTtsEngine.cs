using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Babel.Player.Services.Chatterbox;

internal sealed class ChatterboxTtsEngine : IDisposable
{
    public const int SampleRate = 24000;
    private const long ExaggerationToken = 6563;
    private const long StartSpeechToken = 6561;
    private const long StopSpeechToken = 6562;
    private const long StartTextToken = 255;
    private const long StopTextToken = 0;
    private const long EndOfTextToken = 50256;
    private const long SilenceToken = 4299;
    private const int MaxNewTokens = ChatterboxSampling.MaxNewTokens;
    private const int MinimumDurationBudgetNewTokens = 128;
    private const double SpeechTokensPerSecond = 25.0d;
    private const double DurationBudgetMultiplier = 1.75d;
    private const double DurationBudgetSlackSeconds = 2.0d;
    private const int NumKvHeads = 16;
    private const int HeadDim = 64;

    private readonly AppLog _log;
    private readonly string _modelDir;
    private readonly string? _variant;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private EngineSessions? _sessions;
    private int _disposeSignaled;

    public ChatterboxTtsEngine(AppLog log, string modelDir, string? variant = null)
    {
        _log = log;
        _modelDir = modelDir;
        _variant = variant;
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        string languageCode,
        string referenceAudioPath,
        double? targetDurationSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var modelFiles = ChatterboxModelFiles.Resolve(_modelDir, _variant);
        var referenceAudio = await ChatterboxAudio.LoadMonoFloat32ResampledAsync(
            referenceAudioPath, SampleRate, cancellationToken).ConfigureAwait(false);

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var sessions = GetOrCreateSessions(modelFiles);

            var normalizedText = NormalizeTextForLanguage(
                ChatterboxSampling.NormalizePromptText(text),
                languageCode);
            var conditionedText = ApplyMultilingualLanguagePrefix(normalizedText, languageCode, modelFiles.IsMultilingual);
            var inputIds = BuildTextInputIds(conditionedText, sessions.Tokenizer, modelFiles.IsTurbo);
            var generation = GenerateSpeechTokens(
                inputIds,
                ChatterboxAudio.TruncateReferenceAudio(referenceAudio, SampleRate),
                sessions.SpeechEncoder,
                sessions.EmbedTokens,
                sessions.LanguageModel,
                targetDurationSeconds,
                cancellationToken);
            var audioSamples = DecodeSpeechTokens(
                generation,
                sessions.ConditionalDecoder,
                modelFiles.IsTurbo);
            _log.Info(
                $"Chatterbox synthesized {generation.GeneratedTokens.Length - 1} speech tokens " +
                $"(stop={generation.StoppedOnEos}) for '{conditionedText[..Math.Min(48, conditionedText.Length)]}'.");
            return ChatterboxAudio.EncodeMonoPcm16(audioSamples, SampleRate);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) == 1)
            return;

        if (_sessionGate.Wait(TimeSpan.FromSeconds(10)))
        {
            try
            {
                _sessions?.Dispose();
            }
            finally
            {
                _sessions = null;
                _sessionGate.Release();
                _sessionGate.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeSignaled) != 0)
            throw new ObjectDisposedException(nameof(ChatterboxTtsEngine));
    }

    private EngineSessions GetOrCreateSessions(ChatterboxModelFiles modelFiles)
    {
        if (_sessions is not null &&
            string.Equals(_sessions.ModelRootDirectory, modelFiles.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return _sessions;
        }

        _sessions?.Dispose();
        _log.Info($"Loading Chatterbox sessions from {modelFiles.RootDirectory} (CPU, {ResolveOnnxIntraOpNumThreads()} threads).");
        var tokenizer = ChatterboxTokenizer.LoadAsync(
            Path.Combine(modelFiles.RootDirectory, "tokenizer.json")).GetAwaiter().GetResult();
        _sessions = new EngineSessions(
            modelFiles.RootDirectory,
            tokenizer,
            CreateSession(modelFiles.SpeechEncoderPath),
            CreateSession(modelFiles.EmbedTokensPath),
            CreateSession(modelFiles.LanguageModelPath),
            CreateSession(modelFiles.ConditionalDecoderPath));
        _log.Info("Chatterbox sessions ready.");
        return _sessions;
    }

    internal static int ResolveOnnxIntraOpNumThreads() =>
        Math.Max(1, Environment.ProcessorCount);

    private InferenceSession CreateSession(string modelPath)
    {
        _log.Info($"Loading Chatterbox ONNX graph {Path.GetFileName(modelPath)}.");
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = ResolveOnnxIntraOpNumThreads(),
        };
        return new InferenceSession(modelPath, options);
    }

    private static ChatterboxGenerationResult GenerateSpeechTokens(
        long[] textInputIds,
        float[] referenceAudio,
        InferenceSession speechEncoderSession,
        InferenceSession embedTokensSession,
        InferenceSession languageModelSession,
        double? targetDurationSeconds,
        CancellationToken cancellationToken)
    {
        bool embedNeedsPositionIds = embedTokensSession.InputMetadata.ContainsKey("position_ids");
        bool embedNeedsExaggeration = embedTokensSession.InputMetadata.ContainsKey("exaggeration");
        bool languageNeedsPositionIds = languageModelSession.InputMetadata.ContainsKey("position_ids");

        long[] currentInputIds = textInputIds;
        long[]? embedPositionIds = embedNeedsPositionIds ? BuildInitialBaseEmbedPositionIds(textInputIds) : null;
        long[] generatedTokens = [StartSpeechToken];
        long[] attentionMask = [];
        long[]? languagePositionIds = null;
        PastTensor[] pastKeyValues = [];
        string[] pastKeyNames = languageModelSession.InputMetadata.Keys
            .Where(static name => name.Contains("past_key_values", StringComparison.Ordinal))
            .ToArray();
        long[]? promptTokenIds = null;
        float[]? speakerEmbeddings = null;
        float[]? speakerFeatures = null;
        int[]? speakerEmbeddingsDimensions = null;
        int[]? speakerFeaturesDimensions = null;
        int batchSize = ChatterboxSampling.CfgBatchSize;
        int sequenceLength = 0;
        int languagePosition = 0;
        bool stoppedOnEos = false;
        float[]? condLogits = null;
        float[]? uncondLogits = null;

        int maxNewTokens = ResolveMaxNewTokens(targetDurationSeconds);
        for (int iteration = 0; iteration < maxNewTokens; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenEmbeds = RunEmbedTokens(
                embedTokensSession,
                currentInputIds,
                embedPositionIds,
                embedNeedsExaggeration);

            TensorData<float> inputsEmbeds;
            if (iteration == 0)
            {
                using var speechEncoderInputs = new NamedOnnxValueSet();
                speechEncoderInputs.Add(CreateFloatInput(
                    speechEncoderSession,
                    "audio_values",
                    referenceAudio,
                    [1, referenceAudio.Length]));
                using var speechResults = speechEncoderSession.Run(speechEncoderInputs.Values);
                var outputs = speechResults.ToArray();
                var condEmbeds = RepeatBatch(ReadFloatTensor(outputs[0]), batchSize);
                var promptTensor = outputs[1].AsTensor<long>();
                promptTokenIds = promptTensor.ToArray();
                var speakerEmbeddingTensor = ReadFloatTensor(outputs[2]);
                var speakerFeatureTensor = ReadFloatTensor(outputs[3]);
                speakerEmbeddings = speakerEmbeddingTensor.Values;
                speakerFeatures = speakerFeatureTensor.Values;
                speakerEmbeddingsDimensions = speakerEmbeddingTensor.Dimensions;
                speakerFeaturesDimensions = speakerFeatureTensor.Dimensions;
                var textEmbeds = RepeatBatch(tokenEmbeds, batchSize);
                ZeroTextConditionForCfg(textEmbeds, batchIndex: 1, preservedSuffixTokens: 2);
                inputsEmbeds = ConcatenateEmbeddings(condEmbeds, textEmbeds);
                sequenceLength = inputsEmbeds.Dimensions[1];
                languagePosition = sequenceLength;
                attentionMask = CreateOnesMask(batchSize, sequenceLength);
                if (languageNeedsPositionIds)
                    languagePositionIds = CreatePositionIds(batchSize, sequenceLength, start: 0);

                pastKeyValues = pastKeyNames
                    .Select(name => CreateEmptyPastTensor(name, languageModelSession.InputMetadata[name], batchSize))
                    .ToArray();
            }
            else
            {
                inputsEmbeds = RepeatBatch(tokenEmbeds, batchSize);
                sequenceLength++;
                attentionMask = CreateOnesMask(batchSize, sequenceLength);
                if (languageNeedsPositionIds)
                    languagePositionIds = CreatePositionIds(batchSize, 1, start: languagePosition);
                languagePosition++;
            }

            using var languageInputs = new NamedOnnxValueSet();
            languageInputs.Add(CreateFloatInput(
                languageModelSession,
                "inputs_embeds",
                inputsEmbeds.Values,
                inputsEmbeds.Dimensions));
            languageInputs.Add(NamedOnnxValue.CreateFromTensor(
                "attention_mask",
                new DenseTensor<long>(attentionMask, [batchSize, sequenceLength])));
            if (languageNeedsPositionIds && languagePositionIds is not null)
            {
                int positionSequence = languagePositionIds.Length / batchSize;
                languageInputs.Add(NamedOnnxValue.CreateFromTensor(
                    "position_ids",
                    new DenseTensor<long>(languagePositionIds, [batchSize, positionSequence])));
            }

            foreach (var past in pastKeyValues)
                languageInputs.Add(past.CreateInput());

            using var languageResults = languageModelSession.Run(languageInputs.Values);
            var languageOutputs = languageResults.ToArray();
            var logits = ReadFloatTensor(languageOutputs[0]);
            int vocabSize = logits.Dimensions[^1];
            condLogits ??= new float[vocabSize];
            uncondLogits ??= new float[vocabSize];
            ChatterboxSampling.CopyLastLogits(logits.Values, logits.Dimensions, 0, condLogits);
            ChatterboxSampling.CopyLastLogits(logits.Values, logits.Dimensions, 1, uncondLogits);
            long nextToken = ChatterboxSampling.SampleNextToken(
                condLogits,
                uncondLogits,
                generatedTokens,
                StopSpeechToken,
                Random.Shared.NextDouble());
            generatedTokens = generatedTokens.Concat([nextToken]).ToArray();
            if (nextToken == StopSpeechToken)
            {
                stoppedOnEos = true;
                break;
            }

            currentInputIds = [nextToken];
            if (embedNeedsPositionIds)
                embedPositionIds = [iteration + 1];

            pastKeyValues = languageOutputs
                .Skip(1)
                .Select(output =>
                {
                    var pastName = MapLanguageModelPresentOutputToPastInputName(output.Name);
                    if (!languageModelSession.InputMetadata.TryGetValue(pastName, out var pastMetadata))
                    {
                        throw new InvalidOperationException(
                            $"Chatterbox language model output '{output.Name}' maps to '{pastName}', but that past KV input was not found. " +
                            $"Known past inputs: {string.Join(", ", pastKeyNames.OrderBy(static n => n, StringComparer.Ordinal))}.");
                    }

                    return PastTensor.FromOutput(pastName, pastMetadata, output);
                })
                .ToArray();
        }

        if (promptTokenIds is null || speakerEmbeddings is null || speakerFeatures is null ||
            speakerEmbeddingsDimensions is null || speakerFeaturesDimensions is null)
        {
            throw new InvalidOperationException("Chatterbox speech encoder did not produce reference conditioning tensors.");
        }

        return new ChatterboxGenerationResult(
            generatedTokens,
            promptTokenIds,
            speakerEmbeddings,
            speakerEmbeddingsDimensions,
            speakerFeatures,
            speakerFeaturesDimensions,
            stoppedOnEos);
    }

    private static float[] DecodeSpeechTokens(
        ChatterboxGenerationResult generation,
        InferenceSession decoderSession,
        bool isTurbo)
    {
        long[] speechTokens = generation.GeneratedTokens
            .Skip(1)
            .TakeWhile(static token => token != StopSpeechToken)
            .ToArray();
        if (speechTokens.Length > 1)
            speechTokens = speechTokens[..^1];
        if (isTurbo)
            speechTokens = speechTokens.Concat(Enumerable.Repeat(SilenceToken, 3)).ToArray();

        long[] decoderSpeechTokens = generation.PromptTokenIds.Concat(speechTokens).ToArray();
        using var inputs = new NamedOnnxValueSet();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "speech_tokens",
            new DenseTensor<long>(decoderSpeechTokens, [1, decoderSpeechTokens.Length])));
        inputs.Add(CreateFloatInput(
            decoderSession,
            "speaker_embeddings",
            generation.SpeakerEmbeddings,
            generation.SpeakerEmbeddingsDimensions));
        inputs.Add(CreateFloatInput(
            decoderSession,
            "speaker_features",
            generation.SpeakerFeatures,
            generation.SpeakerFeaturesDimensions));

        using var results = decoderSession.Run(inputs.Values);
        return ReadFloatTensor(results.Single()).Values;
    }

    internal static string ApplyMultilingualLanguagePrefix(
        string text,
        string languageCode,
        bool isMultilingual)
    {
        if (!isMultilingual)
            return text;

        var normalized = string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();

        if (normalized is null || !ChatterboxModelCatalog.SupportedLanguages.Contains(normalized))
        {
            throw new NotSupportedException(
                $"Chatterbox multilingual synthesis requires a supported language code; '{languageCode}' is not in the supported set.");
        }

        return $"[{normalized}]{text}";
    }

    internal static string NormalizeTextForLanguage(string text, string languageCode)
    {
        if (!string.Equals(languageCode?.Trim(), "ko", StringComparison.OrdinalIgnoreCase))
            return text;

        // Korean Jamo decomposition, mirroring korean_normalize in the upstream reference
        // script. Japanese (pykakasi), Hebrew (dicta) and Chinese (Cangjie + pkuseg) need
        // data-driven preprocessing that is not ported yet; those languages synthesize
        // without normalization until their pipelines land.
        var builder = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            if (c < '\uAC00' || c > '\uD7AF')
            {
                builder.Append(c);
                continue;
            }

            int syllable = c - 0xAC00;
            builder.Append((char)(0x1100 + syllable / (21 * 28)));
            builder.Append((char)(0x1161 + (syllable % (21 * 28)) / 28));
            int tail = syllable % 28;
            if (tail > 0)
                builder.Append((char)(0x11A7 + tail));
        }

        return builder.ToString().Trim();
    }

    internal static long[] BuildTextInputIds(long[] tokenIds, bool isTurbo) =>
        isTurbo
            ? [.. tokenIds, EndOfTextToken, EndOfTextToken]
            : [ExaggerationToken, StartTextToken, .. tokenIds, StopTextToken, StartSpeechToken, StartSpeechToken];

    internal static long[] BuildTextInputIds(string text, ChatterboxTokenizer tokenizer, bool isTurbo) =>
        BuildTextInputIds(tokenizer.Encode(text), isTurbo);

    internal static int ResolveMaxNewTokens(double? targetDurationSeconds)
    {
        if (targetDurationSeconds is not double seconds ||
            !double.IsFinite(seconds) ||
            seconds <= 0d)
        {
            return MaxNewTokens;
        }

        double budgetSeconds = Math.Max(
            seconds * DurationBudgetMultiplier,
            seconds + DurationBudgetSlackSeconds);
        int budgetTokens = (int)Math.Ceiling(budgetSeconds * SpeechTokensPerSecond);
        return Math.Clamp(budgetTokens, MinimumDurationBudgetNewTokens, MaxNewTokens);
    }

    internal static string MapLanguageModelPresentOutputToPastInputName(string outputName)
    {
        if (outputName.Contains("present_key_values", StringComparison.Ordinal))
            return outputName.Replace("present_key_values", "past_key_values", StringComparison.Ordinal);

        const string shortPresentPrefix = "present.";
        if (outputName.StartsWith(shortPresentPrefix, StringComparison.Ordinal))
            return string.Concat("past_key_values.", outputName.AsSpan(shortPresentPrefix.Length));

        return outputName.Replace("present", "past", StringComparison.Ordinal);
    }

    private static long[] BuildInitialBaseEmbedPositionIds(long[] inputIds)
    {
        var positionIds = new long[inputIds.Length];
        for (int index = 0; index < inputIds.Length; index++)
            positionIds[index] = inputIds[index] >= StartSpeechToken ? 0 : index - 1L;

        return positionIds;
    }

    private static TensorData<float> RepeatBatch(TensorData<float> source, int batchCount)
    {
        if (source.Dimensions.Length != 3)
            throw new InvalidOperationException("Chatterbox embeddings must be [batch, sequence, hidden].");
        if (source.Dimensions[0] != 1)
            throw new InvalidOperationException("CFG batch expansion expects a single source batch.");
        ArgumentOutOfRangeException.ThrowIfLessThan(batchCount, 1);

        int sequence = source.Dimensions[1];
        int hidden = source.Dimensions[2];
        var values = new float[checked(batchCount * sequence * hidden)];
        for (int batch = 0; batch < batchCount; batch++)
            Array.Copy(source.Values, 0, values, batch * sequence * hidden, source.Values.Length);
        return new TensorData<float>(values, [batchCount, sequence, hidden]);
    }

    private static void ZeroTextConditionForCfg(TensorData<float> tensor, int batchIndex, int preservedSuffixTokens)
    {
        if (tensor.Dimensions.Length != 3)
            throw new InvalidOperationException("Chatterbox embeddings must be [batch, sequence, hidden].");
        if (batchIndex < 0 || batchIndex >= tensor.Dimensions[0])
            throw new ArgumentOutOfRangeException(nameof(batchIndex));

        int sequence = tensor.Dimensions[1];
        int hidden = tensor.Dimensions[2];
        int zeroTokens = Math.Max(0, sequence - Math.Max(0, preservedSuffixTokens));
        Array.Clear(tensor.Values, batchIndex * sequence * hidden, zeroTokens * hidden);
    }

    private static void ZeroBatch(TensorData<float> tensor, int batchIndex)
    {
        ZeroTextConditionForCfg(tensor, batchIndex, preservedSuffixTokens: 0);
    }

    private static long[] CreateOnesMask(int batchSize, int sequenceLength)
    {
        var mask = new long[checked(batchSize * sequenceLength)];
        Array.Fill(mask, 1L);
        return mask;
    }

    private static long[] CreatePositionIds(int batchSize, int sequenceLength, int start)
    {
        var positions = new long[checked(batchSize * sequenceLength)];
        for (int batch = 0; batch < batchSize; batch++)
        {
            for (int index = 0; index < sequenceLength; index++)
                positions[(batch * sequenceLength) + index] = start + index;
        }

        return positions;
    }

    private static TensorData<float> RunEmbedTokens(
        InferenceSession session,
        long[] inputIds,
        long[]? positionIds,
        bool needsExaggeration)
    {
        using var inputs = new NamedOnnxValueSet();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "input_ids",
            new DenseTensor<long>(inputIds, [1, inputIds.Length])));
        if (positionIds is not null && session.InputMetadata.ContainsKey("position_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "position_ids",
                new DenseTensor<long>(positionIds, [1, positionIds.Length])));
        }

        if (needsExaggeration)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "exaggeration",
                new DenseTensor<float>(new[] { 0.5f }, [1])));
        }

        using var results = session.Run(inputs.Values);
        return ReadFloatTensor(results.Single());
    }

    private static TensorData<float> ConcatenateEmbeddings(TensorData<float> left, TensorData<float> right)
    {
        if (left.Dimensions.Length != 3 || right.Dimensions.Length != 3 ||
            left.Dimensions[0] != right.Dimensions[0] ||
            left.Dimensions[2] != right.Dimensions[2])
        {
            throw new InvalidOperationException("Chatterbox embedding tensors must have compatible [batch, sequence, hidden] shapes.");
        }

        int batch = left.Dimensions[0];
        int leftSequence = left.Dimensions[1];
        int rightSequence = right.Dimensions[1];
        int hidden = left.Dimensions[2];
        float[] values = new float[checked(batch * (leftSequence + rightSequence) * hidden)];
        for (int batchIndex = 0; batchIndex < batch; batchIndex++)
        {
            int leftOffset = batchIndex * leftSequence * hidden;
            int rightOffset = batchIndex * rightSequence * hidden;
            int destinationOffset = batchIndex * (leftSequence + rightSequence) * hidden;
            Array.Copy(left.Values, leftOffset, values, destinationOffset, leftSequence * hidden);
            Array.Copy(right.Values, rightOffset, values, destinationOffset + leftSequence * hidden, rightSequence * hidden);
        }

        return new TensorData<float>(values, [batch, leftSequence + rightSequence, hidden]);
    }

    private static TensorData<float> ReadFloatTensor(DisposableNamedOnnxValue value)
    {
        if (value.Value is Tensor<Float16> onnxRuntimeFloat16Tensor)
        {
            Float16[] halfValues = onnxRuntimeFloat16Tensor.ToArray();
            float[] values = new float[halfValues.Length];
            for (int index = 0; index < halfValues.Length; index++)
                values[index] = (float)halfValues[index];

            return new TensorData<float>(values, onnxRuntimeFloat16Tensor.Dimensions.ToArray());
        }

        if (value.Value is Tensor<Half> halfTensor)
        {
            Half[] halfValues = halfTensor.ToArray();
            float[] values = new float[halfValues.Length];
            for (int index = 0; index < halfValues.Length; index++)
                values[index] = (float)halfValues[index];

            return new TensorData<float>(values, halfTensor.Dimensions.ToArray());
        }

        var tensor = value.AsTensor<float>();
        return new TensorData<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static PastTensor CreateEmptyPastTensor(string name, NodeMetadata metadata, int batchSize)
    {
        int[] dimensions = [batchSize, NumKvHeads, 0, HeadDim];
        return metadata.ElementType == typeof(Half)
            ? new PastTensor(name, FloatTensorElementKind.SystemHalf, [], dimensions)
            : metadata.ElementType == typeof(Float16)
                ? new PastTensor(name, FloatTensorElementKind.OnnxRuntimeFloat16, [], dimensions)
                : new PastTensor(name, FloatTensorElementKind.Float32, [], dimensions);
    }

    private static NamedOnnxValue CreateFloatInput(
        InferenceSession session,
        string name,
        float[] values,
        int[] dimensions)
    {
        if (!session.InputMetadata.TryGetValue(name, out var metadata))
            throw new InvalidOperationException($"Chatterbox model input '{name}' was not found.");

        return CreateFloatInput(name, metadata.ElementType, values, dimensions);
    }

    private static NamedOnnxValue CreateFloatInput(
        string name,
        Type elementType,
        float[] values,
        int[] dimensions)
    {
        if (elementType == typeof(Half))
        {
            var halfValues = new Half[values.Length];
            for (int index = 0; index < values.Length; index++)
                halfValues[index] = (Half)values[index];

            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Half>(halfValues, dimensions));
        }

        if (elementType == typeof(Float16))
        {
            var halfValues = new Float16[values.Length];
            for (int index = 0; index < values.Length; index++)
                halfValues[index] = (Float16)values[index];

            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(halfValues, dimensions));
        }

        if (elementType == typeof(float))
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, dimensions));

        throw new InvalidOperationException(
            $"Chatterbox model input '{name}' must be float32 or float16, but declared '{elementType.Name}'.");
    }

    private sealed record ChatterboxGenerationResult(
        long[] GeneratedTokens,
        long[] PromptTokenIds,
        float[] SpeakerEmbeddings,
        int[] SpeakerEmbeddingsDimensions,
        float[] SpeakerFeatures,
        int[] SpeakerFeaturesDimensions,
        bool StoppedOnEos);

    private sealed class EngineSessions : IDisposable
    {
        public EngineSessions(
            string modelRootDirectory,
            ChatterboxTokenizer tokenizer,
            InferenceSession speechEncoder,
            InferenceSession embedTokens,
            InferenceSession languageModel,
            InferenceSession conditionalDecoder)
        {
            ModelRootDirectory = modelRootDirectory;
            Tokenizer = tokenizer;
            SpeechEncoder = speechEncoder;
            EmbedTokens = embedTokens;
            LanguageModel = languageModel;
            ConditionalDecoder = conditionalDecoder;
        }

        public string ModelRootDirectory { get; }
        public ChatterboxTokenizer Tokenizer { get; }
        public InferenceSession SpeechEncoder { get; }
        public InferenceSession EmbedTokens { get; }
        public InferenceSession LanguageModel { get; }
        public InferenceSession ConditionalDecoder { get; }

        public void Dispose()
        {
            SpeechEncoder.Dispose();
            EmbedTokens.Dispose();
            LanguageModel.Dispose();
            ConditionalDecoder.Dispose();
        }
    }

    private sealed class NamedOnnxValueSet : IDisposable
    {
        private readonly List<NamedOnnxValue> _values = new();

        public IReadOnlyList<NamedOnnxValue> Values => _values;

        public void Add(NamedOnnxValue value) => _values.Add(value);

        public void Dispose()
        {
            foreach (var value in _values.OfType<IDisposable>())
                value.Dispose();

            _values.Clear();
        }
    }

    private sealed record TensorData<T>(T[] Values, int[] Dimensions);

    private sealed class PastTensor
    {
        private readonly string _name;
        private readonly FloatTensorElementKind _kind;
        private readonly float[] _values;
        private readonly int[] _dimensions;

        public PastTensor(string name, FloatTensorElementKind kind, float[] values, int[] dimensions)
        {
            _name = name;
            _kind = kind;
            _values = values;
            _dimensions = dimensions;
        }

        public static PastTensor FromOutput(string pastName, NodeMetadata metadata, DisposableNamedOnnxValue output)
        {
            var data = ReadFloatTensor(output);
            var kind = metadata.ElementType == typeof(Half)
                ? FloatTensorElementKind.SystemHalf
                : metadata.ElementType == typeof(Float16)
                    ? FloatTensorElementKind.OnnxRuntimeFloat16
                    : FloatTensorElementKind.Float32;
            return new PastTensor(pastName, kind, data.Values, data.Dimensions);
        }

        public NamedOnnxValue CreateInput()
        {
            if (_kind == FloatTensorElementKind.SystemHalf)
            {
                var halfValues = new Half[_values.Length];
                for (int index = 0; index < _values.Length; index++)
                    halfValues[index] = (Half)_values[index];

                return NamedOnnxValue.CreateFromTensor(_name, new DenseTensor<Half>(halfValues, _dimensions));
            }

            if (_kind == FloatTensorElementKind.OnnxRuntimeFloat16)
            {
                var halfValues = new Float16[_values.Length];
                for (int index = 0; index < _values.Length; index++)
                    halfValues[index] = (Float16)_values[index];

                return NamedOnnxValue.CreateFromTensor(_name, new DenseTensor<Float16>(halfValues, _dimensions));
            }

            return NamedOnnxValue.CreateFromTensor(_name, new DenseTensor<float>(_values, _dimensions));
        }
    }

    private enum FloatTensorElementKind
    {
        Float32,
        SystemHalf,
        OnnxRuntimeFloat16,
    }
}
