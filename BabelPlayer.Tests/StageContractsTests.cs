using System;
using System.Collections.Generic;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for stage contract records (TranscriptionRequest, TranslationRequest, TtsRequest, etc).
/// </summary>
public sealed class StageContractsTests
{
    // ?? TranscriptionRequest ???????????????????????????????????????????????????

    [Fact]
    public void TranscriptionRequest_CreatesValidRecord()
    {
        var request = new TranscriptionRequest(
            "input.wav",
            "output.json",
            "whisper-base",
            "en",
            "int8",
            4,
            1);

        Assert.Equal("input.wav", request.SourceAudioPath);
        Assert.Equal("output.json", request.OutputJsonPath);
        Assert.Equal("whisper-base", request.ModelName);
        Assert.Equal("en", request.LanguageHint);
        Assert.Equal("int8", request.CpuComputeType);
        Assert.Equal(4, request.CpuThreads);
        Assert.Equal(1, request.NumWorkers);
    }

    [Fact]
    public void TranscriptionRequest_SupportsDefaults()
    {
        var request = new TranscriptionRequest(
            "input.wav",
            "output.json",
            "whisper-base");

        Assert.Null(request.LanguageHint);
        Assert.Equal("int8", request.CpuComputeType);
        Assert.Equal(0, request.CpuThreads);
        Assert.Equal(1, request.NumWorkers);
    }

    // ?? TranscriptionResult ????????????????????????????????????????????????????

    [Fact]
    public void TranscriptionResult_CreatesValidRecord()
    {
        var segments = new List<TranscriptSegment>
        {
            new(0.0, 2.5, "Hello world"),
            new(2.5, 5.0, "How are you?")
        };

        var result = new TranscriptionResult(
            true,
            segments,
            "en",
            0.95,
            null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("en", result.Language);
        Assert.Equal(0.95, result.LanguageProbability);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void TranscriptionResult_StoresErrorMessage()
    {
        var result = new TranscriptionResult(
            false,
            [],
            "unknown",
            0.0,
            "Processing failed");

        Assert.False(result.Success);
        Assert.Equal("Processing failed", result.ErrorMessage);
    }

    [Fact]
    public void TranscriptionResult_MetricFields_DefaultToSentinelValues()
    {
        var result = new TranscriptionResult(true, [], "en", 0.99, null);

        Assert.Equal(0L, result.ElapsedMs);
        Assert.Equal(-1.0, result.PeakVramMb);
        Assert.Equal(-1.0, result.PeakRamMb);
    }

    [Fact]
    public void TranscriptionResult_MetricFields_StoreSuppliedValues()
    {
        var result = new TranscriptionResult(
            true, [], "en", 0.99, null,
            ElapsedMs: 1234L,
            PeakVramMb: 512.5,
            PeakRamMb: 1024.0);

        Assert.Equal(1234L, result.ElapsedMs);
        Assert.Equal(512.5, result.PeakVramMb);
        Assert.Equal(1024.0, result.PeakRamMb);
    }

    // ?? TranscriptSegment ??????????????????????????????????????????????????????

    [Fact]
    public void TranscriptSegment_CreatesValidRecord()
    {
        var segment = new TranscriptSegment(0.0, 2.5, "Hello world", "speaker_001");

        Assert.Equal(0.0, segment.StartSeconds);
        Assert.Equal(2.5, segment.EndSeconds);
        Assert.Equal("Hello world", segment.Text);
        Assert.Equal("speaker_001", segment.SpeakerId);
    }

    [Fact]
    public void TranscriptSegment_SupportsNullSpeakerId()
    {
        var segment = new TranscriptSegment(0.0, 2.5, "Hello world");

        Assert.Null(segment.SpeakerId);
    }

    // ?? TranslationRequest ?????????????????????????????????????????????????????

    [Fact]
    public void TranslationRequest_CreatesValidRecord()
    {
        var request = new TranslationRequest(
            "transcript.json",
            "translation.json",
            "es",
            "en",
            "googletrans");

        Assert.Equal("transcript.json", request.TranscriptJsonPath);
        Assert.Equal("translation.json", request.OutputJsonPath);
        Assert.Equal("es", request.SourceLanguage);
        Assert.Equal("en", request.TargetLanguage);
        Assert.Equal("googletrans", request.ModelName);
    }

    // ?? SingleSegmentTranslationTextRequest ????????????????????????????????????

    [Fact]
    public void SingleSegmentTranslationTextRequest_CreatesValidRecord()
    {
        var request = new SingleSegmentTranslationTextRequest(
            "Hola mundo",
            "segment_0.0",
            "es",
            "en",
            "googletrans");

        Assert.Equal("Hola mundo", request.SourceText);
        Assert.Equal("segment_0.0", request.SegmentId);
        Assert.Equal("es", request.SourceLanguage);
        Assert.Equal("en", request.TargetLanguage);
        Assert.Equal("googletrans", request.ModelName);
    }

    // ?? TranslationResult ??????????????????????????????????????????????????????

    [Fact]
    public void TranslationResult_CreatesValidRecord()
    {
        var segments = new List<TranslatedSegment>
        {
            new(0.0, 2.5, "Hola mundo", "Hello world"),
            new(2.5, 5.0, "�C�mo est�s?", "How are you?")
        };

        var result = new TranslationResult(
            true,
            segments,
            "es",
            "en",
            null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("es", result.SourceLanguage);
        Assert.Equal("en", result.TargetLanguage);
        Assert.Null(result.ErrorMessage);
    }

    // ?? TranslatedSegment ??????????????????????????????????????????????????????

    [Fact]
    public void TranslatedSegment_CreatesValidRecord()
    {
        var segment = new TranslatedSegment(
            0.0,
            2.5,
            "Hola mundo",
            "Hello world",
            "speaker_001");

        Assert.Equal(0.0, segment.StartSeconds);
        Assert.Equal(2.5, segment.EndSeconds);
        Assert.Equal("Hola mundo", segment.Text);
        Assert.Equal("Hello world", segment.TranslatedText);
        Assert.Equal("speaker_001", segment.SpeakerId);
    }

    // ?? TtsRequest ?????????????????????????????????????????????????????????????

    [Fact]
    public void TtsRequest_CreatesValidRecord()
    {
        var voiceAssignments = new Dictionary<string, string>
        {
            ["speaker_001"] = "en-US-AriaNeural"
        };

        var request = new TtsRequest(
            "translation.json",
            "output.mp3",
            "en-US-AriaNeural",
            voiceAssignments,
            null,
            "en-US-GuyNeural");

        Assert.Equal("translation.json", request.TranslationJsonPath);
        Assert.Equal("output.mp3", request.OutputAudioPath);
        Assert.Equal("en-US-AriaNeural", request.VoiceName);
        Assert.NotNull(request.SpeakerVoiceAssignments);
        Assert.Equal("en-US-GuyNeural", request.DefaultVoiceFallback);
    }

    // ?? SingleSegmentTtsRequest ????????????????????????????????????????????????

    [Fact]
    public void SingleSegmentTtsRequest_CreatesValidRecord()
    {
        var request = new SingleSegmentTtsRequest(
            "Hello world",
            "output.wav",
            "en-US-AriaNeural",
            "speaker_001",
            "reference.wav",
            "This is a reference");

        Assert.Equal("Hello world", request.Text);
        Assert.Equal("output.wav", request.OutputAudioPath);
        Assert.Equal("en-US-AriaNeural", request.VoiceName);
        Assert.Equal("speaker_001", request.SpeakerId);
        Assert.Equal("reference.wav", request.ReferenceAudioPath);
        Assert.Equal("This is a reference", request.ReferenceTranscriptText);
        Assert.Null(request.TargetDurationSeconds);
        Assert.Null(request.SourceStartSeconds);
        Assert.Null(request.SourceEndSeconds);

        var timed = request with
        {
            TargetDurationSeconds = 8.2,
            SourceStartSeconds = 0,
            SourceEndSeconds = 8.2,
        };
        Assert.Equal(8.2, timed.TargetDurationSeconds);
        Assert.Equal(0, timed.SourceStartSeconds);
        Assert.Equal(8.2, timed.SourceEndSeconds);
    }

    // ?? TtsResult ??????????????????????????????????????????????????????????????

    [Fact]
    public void TtsResult_CreatesValidRecord()
    {
        var result = new TtsResult(
            true,
            "output.mp3",
            "en-US-AriaNeural",
            1024000,
            null);

        Assert.True(result.Success);
        Assert.Equal("output.mp3", result.AudioPath);
        Assert.Equal("en-US-AriaNeural", result.Voice);
        Assert.Equal(1024000, result.FileSizeBytes);
        Assert.Null(result.ErrorMessage);
    }

    // ?? DiarizationRequest ?????????????????????????????????????????????????????

    [Fact]
    public void DiarizationRequest_CreatesValidRecord()
    {
        var request = new DiarizationRequest(
            "audio.wav",
            2,
            4);

        Assert.Equal("audio.wav", request.SourceAudioPath);
        Assert.Equal(2, request.MinSpeakers);
        Assert.Equal(4, request.MaxSpeakers);
    }

    [Fact]
    public void DiarizationRequest_SupportsNullSpeakerCounts()
    {
        var request = new DiarizationRequest("audio.wav");

        Assert.Null(request.MinSpeakers);
        Assert.Null(request.MaxSpeakers);
    }

    // ?? DiarizationResult ??????????????????????????????????????????????????????

    [Fact]
    public void DiarizationResult_CreatesValidRecord()
    {
        var segments = new List<DiarizedSegment>
        {
            new(0.0, 2.5, "speaker_001"),
            new(2.5, 5.0, "speaker_002")
        };

        var result = new DiarizationResult(
            true,
            segments,
            2,
            null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(2, result.SpeakerCount);
        Assert.Null(result.ErrorMessage);
    }

    // ?? DiarizedSegment ????????????????????????????????????????????????????????

    [Fact]
    public void DiarizedSegment_CreatesValidRecord()
    {
        var segment = new DiarizedSegment(0.0, 2.5, "speaker_001");

        Assert.Equal(0.0, segment.StartSeconds);
        Assert.Equal(2.5, segment.EndSeconds);
        Assert.Equal("speaker_001", segment.SpeakerId);
    }
}
