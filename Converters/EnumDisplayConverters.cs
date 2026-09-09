using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Babel.Player.Models;

namespace Babel.Player.Converters;

public sealed class ComputeProfileDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ComputeProfile.Cpu => "CPU",
        ComputeProfile.Gpu => "GPU",
        ComputeProfile.Cloud => "Cloud",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class GpuHostBackendDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        GpuHostBackend.ManagedVenv => "Managed local GPU",
        GpuHostBackend.DockerHost => "Docker GPU host",
        _ => value?.ToString() ?? string.Empty,
    };

    /// <summary>
        /// Conversion back from the target type to the source type is not supported by this converter.
        /// </summary>
        /// <summary>
        /// Indicates that converting from the target back to the source is not supported and will always throw.
        /// </summary>
        /// <exception cref="System.NotSupportedException">Thrown unconditionally because backward conversion is not implemented.</exception>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SegmentTimingModeDisplayConverter : IValueConverter
{
    /// <summary>
    /// Converts a SegmentTimingMode value into its user-facing label.
    /// </summary>
    /// <param name="value">The value to convert; expected to be a <see cref="SegmentTimingMode"/> or null.</param>
    /// <returns>A string label: "Off" for <see cref="SegmentTimingMode.Off"/>, "Stretch" for <see cref="SegmentTimingMode.Stretch"/>, "Pause" for <see cref="SegmentTimingMode.Pause"/>, the original value's <c>ToString()</c> for unrecognized values, or an empty string if <paramref name="value"/> is null.</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SegmentTimingMode.Off => "Off",
        SegmentTimingMode.Stretch => "Stretch",
        SegmentTimingMode.Pause => "Pause",
        _ => value?.ToString() ?? string.Empty,
    };

    /// <summary>
        /// Indicates that converting from the target back to the source is not supported and will always throw.
        /// </summary>
        /// <exception cref="System.NotSupportedException">Thrown unconditionally because backward conversion is not implemented.</exception>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DiarizationProviderDisplayConverter : IValueConverter
{
    /// <summary>
    /// Converts a diarization provider identifier into a user-facing display string without consulting the runtime registry.
    /// </summary>
    /// <param name="value">The provider identifier (may be null, empty, or a ProviderNames value).</param>
    /// <returns>
    /// "Off" when <paramref name="value"/> is null or whitespace; otherwise returns a stable display label for known
    /// diarization provider identifiers and falls back to the raw identifier when the value is unrecognized.
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return "Off";

        if (value is not string providerId || string.IsNullOrWhiteSpace(providerId))
            return "Off";

        return providerId switch
        {
            ProviderNames.NemoLocal => "NeMo",
            ProviderNames.WeSpeakerLocal => "WeSpeaker",
            ProviderNames.SortFormerLocal => "SortFormer",
            _ => providerId,
        };
    }

    /// <summary>
        /// Conversion back from the target type to the source type is not supported by this converter.
        /// </summary>
        /// <exception cref="NotSupportedException">Always thrown to indicate reverse conversion is not implemented.</exception>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SpeakerIdToShortLabelConverter : IValueConverter
{
    /// <summary>
    /// Converts a speaker identifier in the form "spk_<n>" into a short label "S<n>".
    /// </summary>
    /// <returns>
    /// "S<n>" when <paramref name="value"/> is a non-empty string that starts with "spk_" and the suffix parses as an integer; otherwise an empty string.
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string speakerId || string.IsNullOrWhiteSpace(speakerId))
            return string.Empty;

        const string prefix = "spk_";
        if (!speakerId.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(speakerId[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return string.Empty;
        }

        return $"S{index}";
    }

    /// <summary>
        /// Conversion back from the target type to the source type is not supported by this converter.
        /// </summary>
        /// <exception cref="NotSupportedException">Always thrown to indicate reverse conversion is not implemented.</exception>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SpeakerIdToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush FallbackBrush = new(Color.Parse("#374151"));

    private static readonly IReadOnlyList<SolidColorBrush> Palette =
    [
        new(Color.Parse("#2563EB")),
        new(Color.Parse("#059669")),
        new(Color.Parse("#D97706")),
        new(Color.Parse("#DC2626")),
        new(Color.Parse("#7C3AED")),
        new(Color.Parse("#0891B2")),
        new(Color.Parse("#EA580C")),
        new(Color.Parse("#DB2777")),
    ];

    /// <summary>
    /// Converts a speaker identifier of the form "spk_<index>" into a SolidColorBrush from the palette, returning a fallback brush for null, empty, malformed, or non-matching inputs.
    /// </summary>
    /// <param name="value">The speaker identifier (expected to be a string like "spk_0").</param>
    /// <returns>The palette brush corresponding to the parsed speaker index, or the fallback brush if the input is null, empty, does not start with "spk_", or the index cannot be parsed as an integer.</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string speakerId || string.IsNullOrWhiteSpace(speakerId))
            return FallbackBrush;

        const string prefix = "spk_";
        if (!speakerId.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(speakerId[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return FallbackBrush;
        }

        return Palette[index % Palette.Count];
    }

    /// <summary>
        /// Conversion back from the target type to the source type is not supported by this converter.
        /// </summary>
        /// <exception cref="NotSupportedException">Always thrown to indicate reverse conversion is not implemented.</exception>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
