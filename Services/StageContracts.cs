using System;
using System.Collections.Generic;

namespace Babel.Player.Services;

// --- TRANSCRIPTION ---
public sealed record TranscriptionRequest(
    string SourceAudioPath,
    string OutputJsonPath,
    string ModelName,
    string? LanguageHint = null,
    string CpuComputeType = "int8",
    int CpuThreads = 0,
    int NumWorkers = 1);

public sealed record TranscriptionResult(
    bool Success,
    IReadOnlyList<TranscriptSegment> Segments,
    string Language,
    double LanguageProbability,
    string? ErrorMessage,
    long ElapsedMs = 0,
    double PeakVramMb = -1,
    double PeakRamMb = -1);

public sealed record TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string? SpeakerId = null);

// --- TRANSLATION ---
public sealed record TranslationRequest(
    string TranscriptJsonPath, 
    string OutputJsonPath, 
    string SourceLanguage, 
    string TargetLanguage, 
    string ModelName);

public sealed record SingleSegmentTranslationTextRequest(
    string SourceText, 
    string SegmentId, 
    string SourceLanguage, 
    string TargetLanguage, 
    string ModelName);

public sealed record SingleSegmentTranslationTextResult(
    bool Success,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string? ErrorMessage);

public sealed record TranslationResult(
    bool Success,
    IReadOnlyList<TranslatedSegment> Segments,
    string SourceLanguage,
    string TargetLanguage,
    string? ErrorMessage);

public sealed record TranslatedSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string TranslatedText,
    string? SpeakerId = null);

// --- TTS ---
public sealed record TtsRequest(
    string TranslationJsonPath,
    string OutputAudioPath,
    string VoiceName,
    Dictionary<string, string>? SpeakerVoiceAssignments = null,
    Dictionary<string, string>? SpeakerReferenceAudioPaths = null,
    string? DefaultVoiceFallback = null,
    string? Language = null,
    string? SourceVideoPath = null,
    IProgress<(int Completed, int Total)>? SegmentProgress = null);

public sealed record SingleSegmentTtsRequest(
    string Text,
    string OutputAudioPath,
    string VoiceName,
    string? SpeakerId = null,
    string? ReferenceAudioPath = null,
    string? ReferenceTranscriptText = null,
    string? Language = null,
    string? SourceVideoPath = null,
    double? TargetDurationSeconds = null,
    double? SourceStartSeconds = null,
    double? SourceEndSeconds = null);

public sealed record QwenBatchSegmentRequest(
    string SegmentId,
    string Text,
    string OutputAudioPath,
    string VoiceName,
    string? SpeakerId = null,
    string? ReferenceAudioPath = null,
    string? Language = null,
    string? SourceVideoPath = null);

public sealed record TtsResult(
    bool Success,
    string AudioPath,
    string Voice,
    long FileSizeBytes,
    string? ErrorMessage,
    /// <summary>
    /// Duration of the generated audio clip in seconds.
    /// Null when the provider does not report duration.
    /// Populated by providers that return clip duration metadata (e.g. Edge TTS, Piper workers).
    /// </summary>
    double? DurationSeconds = null);

// --- DIARIZATION ---
public sealed record DiarizationRequest(
    string SourceAudioPath,
    int? MinSpeakers = null,
    int? MaxSpeakers = null);

public sealed record DiarizationResult(
    bool Success,
    IReadOnlyList<DiarizedSegment> Segments,
    int SpeakerCount,
    string? ErrorMessage);

public sealed record DiarizedSegment(
    double StartSeconds,
    double EndSeconds,
    string SpeakerId);

// --- VOCAL SEPARATION ---
public sealed record VocalSeparationRequest(
    string SourceAudioPath,
    string OutputDirectoryPath);

public sealed record VocalSeparationResult(
    bool Success,
    string VocalsAudioPath,
    string AmbianceAudioPath,
    string? ErrorMessage,
    long VocalsFileSizeBytes = 0,
    long AmbianceFileSizeBytes = 0,
    string? VocalsModel = null,
    string? AmbianceModel = null);
