using System;
using System.Buffers;
using System.Linq;
using System.Numerics;
using System.Threading;
using MathNet.Numerics.IntegralTransforms;

namespace Babel.Player.Services.SortFormer;

internal sealed class SortFormerFeatureExtractor
{
    public const int SampleRate = 16000;
    public const int FftSize = 512;
    public const int WindowLength = 400;
    public const int HopLength = 160;
    public const int MelBins = 128;

    private const float PreEmphasis = 0.97f;
    private const float LogZeroGuard = 5.9604645e-8f;
    private const int FrequencyBins = 1 + (FftSize / 2);

    private readonly float[] _fftWindow = BuildCenteredPeriodicHannWindow();
    private readonly float[,] _melFilters = BuildMelFilterBank();

    public SortFormerFeatureInputSet Extract(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return new SortFormerFeatureInputSet(Array.Empty<float>(), 0, MelBins);

        const int padAmount = FftSize / 2;
        int paddedLength = samples.Length + (padAmount * 2);
        float[] paddedSamples = ArrayPool<float>.Shared.Rent(paddedLength);

        try
        {
            Array.Clear(paddedSamples, 0, padAmount);

            paddedSamples[padAmount] = samples[0];
            for (int index = 1; index < samples.Length; index++)
                paddedSamples[padAmount + index] = samples[index] - (PreEmphasis * samples[index - 1]);

            Array.Clear(paddedSamples, padAmount + samples.Length, paddedLength - (padAmount + samples.Length));

            int frameCount = 1 + ((paddedLength - FftSize) / HopLength);
            float[] powerSpectrum = ArrayPool<float>.Shared.Rent(FrequencyBins * frameCount);

            try
            {
                var spectrum = new Complex[FftSize];

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    int sampleOffset = frameIndex * HopLength;
                    Array.Clear(spectrum);
                    for (int sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
                    {
                        double windowed = paddedSamples[sampleOffset + sampleIndex] * _fftWindow[sampleIndex];
                        spectrum[sampleIndex] = new Complex(windowed, 0);
                    }

                    Fourier.Forward(spectrum, FourierOptions.Matlab);

                    int frameOffset = frameIndex * FrequencyBins;
                    for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                    {
                        double magnitude = spectrum[binIndex].Magnitude;
                        powerSpectrum[frameOffset + binIndex] = (float)(magnitude * magnitude);
                    }
                }

                int featureElementCount = frameCount * MelBins;
                float[] data = ArrayPool<float>.Shared.Rent(featureElementCount);
                bool transferredOwnership = false;
                try
                {
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        int powerOffset = frameIndex * FrequencyBins;
                        int dataOffset = frameIndex * MelBins;

                        for (int melIndex = 0; melIndex < MelBins; melIndex++)
                        {
                            double sum = 0;
                            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                                sum += _melFilters[melIndex, binIndex] * powerSpectrum[powerOffset + binIndex];

                            data[dataOffset + melIndex] = MathF.Log((float)sum + LogZeroGuard);
                        }
                    }

                    var inputSet = new SortFormerFeatureInputSet(data, frameCount, MelBins);
                    transferredOwnership = true;
                    return inputSet;
                }
                finally
                {
                    if (!transferredOwnership)
                        ArrayPool<float>.Shared.Return(data);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(powerSpectrum);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(paddedSamples);
        }
    }

    private static float[] BuildCenteredPeriodicHannWindow()
    {
        var window = new float[FftSize];
        int offset = (FftSize - WindowLength) / 2;
        for (int index = 0; index < WindowLength; index++)
            window[offset + index] = (float)(0.5d - (0.5d * Math.Cos((2d * Math.PI * index) / WindowLength)));

        return window;
    }

    private static float[,] BuildMelFilterBank()
    {
        var filters = new float[MelBins, FrequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, FrequencyBins)
            .Select(index => (index * (double)SampleRate) / FftSize)
            .ToArray();

        double melMin = HertzToMel(0);
        double melMax = HertzToMel(SampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, MelBins + 2)
            .Select(index => MelToHertz(melMin + ((melMax - melMin) * index / (MelBins + 1d))))
            .ToArray();

        double[] differences = melPoints
            .Zip(melPoints.Skip(1), static (left, right) => right - left)
            .ToArray();

        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double frequency = fftFrequencies[binIndex];
                double lower = (frequency - melPoints[melIndex]) / Math.Max(differences[melIndex], double.Epsilon);
                double upper = (melPoints[melIndex + 2] - frequency) / Math.Max(differences[melIndex + 1], double.Epsilon);
                double weight = Math.Max(0, Math.Min(lower, upper));
                double enorm = 2d / Math.Max(melPoints[melIndex + 2] - melPoints[melIndex], double.Epsilon);
                filters[melIndex, binIndex] = (float)(weight * enorm);
            }
        }

        return filters;
    }

    private static double HertzToMel(double frequencyHertz)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4d) / 27d;

        return frequencyHertz >= minLogHertz
            ? minLogMel + (Math.Log(frequencyHertz / minLogHertz) / logStep)
            : frequencyHertz / fSp;
    }

    private static double MelToHertz(double mel)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4d) / 27d;

        return mel >= minLogMel
            ? minLogHertz * Math.Exp(logStep * (mel - minLogMel))
            : mel * fSp;
    }
}

internal sealed class SortFormerFeatureInputSet : IDisposable
{
    private float[]? _data;
    private readonly int _elementCount;

    public SortFormerFeatureInputSet(float[] data, int frameCount, int featureCount)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        FrameCount = frameCount;
        FeatureCount = featureCount;
        _elementCount = checked(frameCount * featureCount);
    }

    public ReadOnlySpan<float> Data => (_data ?? throw new ObjectDisposedException(nameof(SortFormerFeatureInputSet)))
        .AsSpan(0, _elementCount);

    public int FrameCount { get; }
    public int FeatureCount { get; }

    public void CopyFramesTo(
        float[] destination,
        int destinationFrameOffset,
        int sourceFrameOffset,
        int frameCount)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (frameCount <= 0)
            return;

        float[] source = _data ?? throw new ObjectDisposedException(nameof(SortFormerFeatureInputSet));
        int sourceOffset = sourceFrameOffset * FeatureCount;
        int destinationOffset = destinationFrameOffset * FeatureCount;
        Array.Copy(source, sourceOffset, destination, destinationOffset, frameCount * FeatureCount);
    }

    public void Dispose()
    {
        float[]? rentedData = Interlocked.Exchange(ref _data, null);
        if (rentedData is { Length: > 0 })
            ArrayPool<float>.Shared.Return(rentedData);
    }
}
