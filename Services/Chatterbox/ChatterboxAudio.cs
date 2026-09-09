using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services.Chatterbox;

internal static class ChatterboxAudio
{
    public const int TargetSampleRate = 24000;
    public const int MinimumSpeechEncoderSamples = 1000;

    public static byte[] EncodeMonoPcm16(float[] samples, int sampleRate)
    {
        var dataBytes = new byte[checked(samples.Length * 2)];
        for (int index = 0; index < samples.Length; index++)
        {
            var clamped = Math.Clamp(samples[index], -1f, 1f);
            var pcm = (short)Math.Round(clamped * short.MaxValue);
            dataBytes[checked(index * 2)] = (byte)(pcm & 0xFF);
            dataBytes[checked(index * 2 + 1)] = (byte)((pcm >> 8) & 0xFF);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + dataBytes.Length));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * 2));
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes.Length);
        writer.Write(dataBytes);
        writer.Flush();
        return stream.ToArray();
    }

    public static async Task<float[]> LoadMonoFloat32ResampledAsync(
        string path,
        int targetSampleRate,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var (samples, sourceRate) = DecodePcm16Mono(bytes);
        if (sourceRate != targetSampleRate)
            samples = ResampleLinear(samples, sourceRate, targetSampleRate);
        if (samples.Length < MinimumSpeechEncoderSamples)
        {
            var padded = new float[MinimumSpeechEncoderSamples];
            Array.Copy(samples, padded, samples.Length);
            samples = padded;
        }

        // Single truncation point. Callers must not truncate again.
        return TruncateReferenceAudio(samples, targetSampleRate);
    }

    internal static float[] TruncateReferenceAudio(
        float[] samples,
        int sampleRate,
        double maxSeconds = 10.0d)
    {
        if (samples.Length == 0 || sampleRate <= 0 || maxSeconds <= 0)
            return samples;

        int maxSamples = Math.Max(MinimumSpeechEncoderSamples, (int)Math.Round(sampleRate * maxSeconds));
        if (samples.Length <= maxSamples)
            return samples;

        var truncated = new float[maxSamples];
        Array.Copy(samples, truncated, maxSamples);
        return truncated;
    }

    internal static (float[] Samples, int SampleRate) DecodePcm16Mono(byte[] wavBytes)
    {
        if (wavBytes.Length < 12)
            throw new InvalidDataException("WAV file is too short to contain a valid header.");
        if (Encoding.ASCII.GetString(wavBytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wavBytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a WAV file: missing RIFF/WAVE header.");

        int audioFormat = 0;
        int channelCount = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;
        int position = 12;
        while (position + 8 <= wavBytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wavBytes, position, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, position + 4);
            if (chunkSize < 0 || position + 8 + chunkSize > wavBytes.Length)
                break;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("WAV fmt chunk is too short.");
                audioFormat = BitConverter.ToUInt16(wavBytes, position + 8);
                channelCount = BitConverter.ToUInt16(wavBytes, position + 10);
                sampleRate = BitConverter.ToInt32(wavBytes, position + 12);
                bitsPerSample = BitConverter.ToUInt16(wavBytes, position + 22);
            }
            else if (chunkId == "data" && dataOffset < 0)
            {
                dataOffset = position + 8;
                dataLength = chunkSize;
            }

            position += 8 + chunkSize + (chunkSize & 1);
        }

        if (audioFormat != 1)
            throw new InvalidDataException($"Only PCM WAV is supported, got format {audioFormat}.");
        if (channelCount <= 0)
            throw new InvalidDataException("WAV file must contain at least one channel.");
        if (bitsPerSample != 16)
            throw new InvalidDataException($"Only 16-bit PCM WAV is supported, got {bitsPerSample}-bit.");
        if (sampleRate <= 0)
            throw new InvalidDataException("WAV file has an invalid sample rate.");
        if (dataOffset < 0)
            throw new InvalidDataException("WAV file contains no data chunk.");

        int bytesPerFrame = 2 * channelCount;
        int frameCount = Math.Min(dataLength, wavBytes.Length - dataOffset) / bytesPerFrame;
        var samples = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channelCount; channel++)
            {
                int offset = dataOffset + (frame * channelCount + channel) * 2;
                sum += BitConverter.ToInt16(wavBytes, offset) / (float)short.MaxValue;
            }

            samples[frame] = sum / channelCount;
        }

        return (samples, sampleRate);
    }

    internal static float[] ResampleLinear(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || samples.Length == 0)
            return samples;

        int outputLength = Math.Max(1, (int)Math.Round((double)samples.Length * targetRate / sourceRate));
        var output = new float[outputLength];
        for (int index = 0; index < outputLength; index++)
        {
            double sourcePosition = (double)index * sourceRate / targetRate;
            int lower = (int)Math.Floor(sourcePosition);
            int upper = Math.Min(lower + 1, samples.Length - 1);
            double fraction = sourcePosition - lower;
            output[index] = (float)(samples[Math.Min(lower, samples.Length - 1)] * (1.0 - fraction) + samples[upper] * fraction);
        }

        return output;
    }
}
