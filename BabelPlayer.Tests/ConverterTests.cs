using System.Globalization;
using Avalonia.Media;
using Babel.Player.Converters;
using Babel.Player.Models;

namespace BabelPlayer.Tests;

public sealed class ConverterTests
{
    [Theory]
    [InlineData(null, "Off")]
    [InlineData("", "Off")]
    [InlineData(ProviderNames.NemoLocal, "NeMo")]
    [InlineData(ProviderNames.WeSpeakerLocal, "WeSpeaker")]
    [InlineData(ProviderNames.SortFormerLocal, "SortFormer")]
    [InlineData("custom-provider", "custom-provider")]
    public void DiarizationProviderDisplayConverter_MapsKnownProvidersAndOff(string? providerId, string expected)
    {
        var converter = new DiarizationProviderDisplayConverter();

        var result = converter.Convert(providerId, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("spk_00", "S0")]
    [InlineData("spk_01", "S1")]
    [InlineData("spk_12", "S12")]
    public void SpeakerIdToShortLabelConverter_MapsNormalizedSpeakerIds(string speakerId, string expected)
    {
        var converter = new SpeakerIdToShortLabelConverter();

        var result = converter.Convert(speakerId, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("speaker-1")]
    public void SpeakerIdToShortLabelConverter_ReturnsEmptyForMissingOrInvalidIds(string? speakerId)
    {
        var converter = new SpeakerIdToShortLabelConverter();

        var result = converter.Convert(speakerId, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SpeakerIdToColorConverter_MapsSameSpeakerToStableColor()
    {
        var converter = new SpeakerIdToColorConverter();

        var first = Assert.IsType<SolidColorBrush>(
            converter.Convert("spk_02", typeof(IBrush), null, CultureInfo.InvariantCulture));
        var second = Assert.IsType<SolidColorBrush>(
            converter.Convert("spk_02", typeof(IBrush), null, CultureInfo.InvariantCulture));

        Assert.Equal(first.Color, second.Color);
    }

    [Fact]
    public void SpeakerIdToColorConverter_UsesSpeakerIndexToChoosePaletteColor()
    {
        var converter = new SpeakerIdToColorConverter();

        var first = Assert.IsType<SolidColorBrush>(
            converter.Convert("spk_00", typeof(IBrush), null, CultureInfo.InvariantCulture));
        var second = Assert.IsType<SolidColorBrush>(
            converter.Convert("spk_01", typeof(IBrush), null, CultureInfo.InvariantCulture));

        Assert.NotEqual(first.Color, second.Color);
    }
}
