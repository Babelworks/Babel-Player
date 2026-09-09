using System;
using System.Collections.Generic;

namespace Babel.Player.Services.Chatterbox;

/// <summary>
/// Official Chatterbox multilingual sampling: CFG, temperature, min-p, and repetition control.
/// Cross-lingual clones (source-language reference, target-language text) need this so the
/// text condition can override the reference language instead of hallucinating or looping.
/// </summary>
internal static class ChatterboxSampling
{
    public const float CfgWeight = 0.5f;
    public const float Temperature = 0.8f;
    public const float MinP = 0.05f;
    public const float RepetitionPenalty = 1.2f;
    public const int ConsecutiveRepeatLimit = 16;
    public const int CfgBatchSize = 2;
    public const int MaxNewTokens = 1000;

    internal static string NormalizePromptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "You need to add some text for me to talk.";

        text = text.Trim();
        if (text[0] is >= 'a' and <= 'z')
            text = char.ToUpperInvariant(text[0]) + text[1..];

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = text
            .Replace("...", ", ", StringComparison.Ordinal)
            .Replace("…", ", ", StringComparison.Ordinal)
            .Replace(":", ",", StringComparison.Ordinal)
            .Replace(" - ", ", ", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace('—', '-')
            .Replace('–', '-')
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'');

        var trimmedEnd = text.AsSpan().TrimEnd();
        if (trimmedEnd.IsEmpty || !IsSentenceEnder(trimmedEnd[^1]))
            text += ".";

        return text;
    }

    private static bool IsSentenceEnder(char value) =>
        value is '.' or '!' or '?' or '-' or ',' or '、' or '，' or '。' or '？' or '！';

    internal static int CountTrailingRepeats(IReadOnlyList<long> tokens)
    {
        if (tokens.Count == 0)
            return 0;

        long last = tokens[^1];
        int count = 1;
        for (int index = tokens.Count - 2; index >= 0; index--)
        {
            if (tokens[index] != last)
                break;
            count++;
        }

        return count;
    }

    internal static void ApplyCfg(ReadOnlySpan<float> condLogits, ReadOnlySpan<float> uncondLogits, float cfgWeight, Span<float> destination)
    {
        if (condLogits.Length != destination.Length)
            throw new ArgumentException("CFG logit buffers must match.", nameof(destination));

        if (uncondLogits.IsEmpty || cfgWeight <= 0f)
        {
            condLogits.CopyTo(destination);
            return;
        }

        if (uncondLogits.Length != condLogits.Length)
            throw new ArgumentException("Conditional and unconditional logits must have the same vocabulary size.", nameof(uncondLogits));

        for (int index = 0; index < condLogits.Length; index++)
            destination[index] = condLogits[index] + cfgWeight * (condLogits[index] - uncondLogits[index]);
    }

    internal static void ApplyRepetitionPenalty(
        Span<float> logits,
        IReadOnlyList<long> generatedTokens,
        float penalty)
    {
        if (penalty <= 0f || penalty == 1f || generatedTokens.Count == 0)
            return;

        var seen = new HashSet<long>();
        for (int index = 0; index < generatedTokens.Count; index++)
            seen.Add(generatedTokens[index]);

        foreach (long token in seen)
        {
            if (token < 0 || token >= logits.Length)
                continue;

            int id = (int)token;
            logits[id] = logits[id] < 0f ? logits[id] * penalty : logits[id] / penalty;
        }
    }

    internal static void CopyLastLogits(ReadOnlySpan<float> values, ReadOnlySpan<int> dimensions, int batchIndex, Span<float> destination)
    {
        if (dimensions.Length != 3)
            throw new ArgumentException("Logits must be [batch, sequence, vocab].", nameof(dimensions));

        int batch = dimensions[0];
        int sequence = dimensions[1];
        int vocab = dimensions[2];
        if (batchIndex < 0 || batchIndex >= batch)
            throw new ArgumentOutOfRangeException(nameof(batchIndex));
        if (destination.Length < vocab)
            throw new ArgumentException("Destination is shorter than the vocabulary.", nameof(destination));

        int offset = ((batchIndex * sequence) + (sequence - 1)) * vocab;
        values.Slice(offset, vocab).CopyTo(destination);
    }

    internal static long SampleNextToken(
        ReadOnlySpan<float> condLogits,
        ReadOnlySpan<float> uncondLogits,
        IReadOnlyList<long> generatedTokens,
        long stopSpeechToken,
        double randomValue,
        float cfgWeight = CfgWeight,
        float temperature = Temperature,
        float minP = MinP,
        float repetitionPenalty = RepetitionPenalty,
        int consecutiveRepeatLimit = ConsecutiveRepeatLimit)
    {
        if (condLogits.IsEmpty)
            throw new ArgumentException("Conditional logits are required.", nameof(condLogits));
        if (randomValue is < 0d or >= 1d)
            throw new ArgumentOutOfRangeException(nameof(randomValue), "Random value must be in [0, 1).");

        if (CountTrailingRepeats(generatedTokens) >= consecutiveRepeatLimit)
            return stopSpeechToken;

        Span<float> logits = condLogits.Length <= 16_384
            ? stackalloc float[condLogits.Length]
            : new float[condLogits.Length];
        ApplyCfg(condLogits, uncondLogits, cfgWeight, logits);
        ApplyRepetitionPenalty(logits, generatedTokens, repetitionPenalty);

        if (temperature <= 0f)
            return ArgMax(logits);

        for (int index = 0; index < logits.Length; index++)
            logits[index] /= temperature;

        Span<float> probabilities = logits.Length <= 16_384
            ? stackalloc float[logits.Length]
            : new float[logits.Length];
        Softmax(logits, probabilities);

        float maxProbability = 0f;
        for (int index = 0; index < probabilities.Length; index++)
        {
            if (probabilities[index] > maxProbability)
                maxProbability = probabilities[index];
        }

        float minProbability = minP * maxProbability;
        float survivingMass = 0f;
        for (int index = 0; index < probabilities.Length; index++)
        {
            if (probabilities[index] < minProbability)
                probabilities[index] = 0f;
            else
                survivingMass += probabilities[index];
        }

        if (survivingMass <= 0f)
            return ArgMax(logits);

        double threshold = randomValue * survivingMass;
        double cumulative = 0d;
        for (int index = 0; index < probabilities.Length; index++)
        {
            if (probabilities[index] <= 0f)
                continue;
            cumulative += probabilities[index];
            if (cumulative >= threshold)
                return index;
        }

        return ArgMax(logits);
    }

    private static long ArgMax(ReadOnlySpan<float> logits)
    {
        int best = 0;
        float bestScore = float.NegativeInfinity;
        for (int index = 0; index < logits.Length; index++)
        {
            if (logits[index] > bestScore)
            {
                bestScore = logits[index];
                best = index;
            }
        }

        return best;
    }

    private static void Softmax(ReadOnlySpan<float> logits, Span<float> destination)
    {
        float max = float.NegativeInfinity;
        for (int index = 0; index < logits.Length; index++)
        {
            if (logits[index] > max)
                max = logits[index];
        }

        float sum = 0f;
        for (int index = 0; index < logits.Length; index++)
        {
            float value = MathF.Exp(logits[index] - max);
            destination[index] = value;
            sum += value;
        }

        if (sum <= 0f)
        {
            destination.Clear();
            return;
        }

        float scale = 1f / sum;
        for (int index = 0; index < destination.Length; index++)
            destination[index] *= scale;
    }
}
