# TTS Pipeline Stall — Fix Handoff

This document is a self-contained engineering handoff for Claude Code. It contains a
complete diagnosis and a prioritized, actionable fix list for the TTS pipeline stall
that affects all providers. Read this fully before touching any file.

---

## Background

Babel Player is a dubbing workstation. Its core flow is:

```
source media → transcription → translation → TTS (per-segment) → stitch → dub MP3
```

`SessionWorkflowCoordinator` is the single state owner. It is split into partial files:
- `SessionWorkflowCoordinator.cs` — fields, constructor, main public API
- `SessionWorkflowCoordinator.Pipeline.cs` — stage execution methods, commit helpers
- `SessionWorkflowCoordinator.ExecutionSnapshots.cs` — snapshot prep, revision counters, input-match guards
- `SessionWorkflowCoordinator.Orchestrators.Tts.cs` — `TtsPipelineOrchestrator` inner class
- `SessionWorkflowCoordinator.Orchestrators.Streaming.cs` — `StreamingPipelineOrchestrator` inner class
- `SessionWorkflowCoordinator.TtsReference.cs` — Qwen reference clip extraction helpers
- `SessionWorkflowCoordinator.ExecutionPlanning.cs` — `ResolveAndApplyExecutionPlan`
- `SessionWorkflowCoordinator.SessionState.cs` — session load/restore helpers

Provider implementations live in `Services/`:
- `QwenContainerTtsProvider.cs` — Qwen3-TTS, backed by a local FastAPI server at `http://127.0.0.1:18000`
- `ContainerizedInferenceClient.cs` — HTTP client to the FastAPI server
- `ProviderLease.cs` — `ProviderLeaseManager<T>` and `ProviderLease<T>` — ref-counted provider lifecycle

The Python FastAPI server lives in `inference/main.py`. Relevant endpoints:
- `POST /tts/qwen/references` — registers a reference audio clip, returns a `reference_id`
- `POST /tts/qwen/segment` — synthesizes one segment, returns a server-side temp WAV path
- `GET /tts/audio/{filename}` — downloads the synthesized WAV to the C# client

---

## Symptom

After the pipeline reaches the TTS stage, it appears to run (spinner visible) and then nothing
happens. The stage silently stays at `Translated`. This happens regardless of which TTS provider
is selected (Qwen, Piper, Edge TTS, ElevenLabs, OpenAI TTS). No error is shown to the user.

---

## Root Causes (ranked by impact)

### 1. `TtsInputsStillMatch` silently discards completed TTS runs — HIGHEST PRIORITY

**File:** `Services/SessionWorkflowCoordinator.ExecutionSnapshots.cs`

```csharp
private bool TtsInputsStillMatch(TtsExecutionSnapshot snapshot)
{
    lock (_sessionLock)
    {
        return CurrentSession.SessionId == snapshot.SessionId
            && SessionRevision == snapshot.SessionRevision   // <-- THE PROBLEM
            && snapshot.TranslationIdentity.Matches(CurrentSession.TranslationPath)
            && DictionariesEqual(CurrentSession.SpeakerVoiceAssignments, snapshot.SpeakerVoiceAssignments)
            && DictionariesEqual(CurrentSession.SpeakerReferenceAudioPaths, snapshot.SpeakerReferenceAudioPaths)
            && DictionariesEqual(CurrentSession.SegmentTimingModeOverrides, snapshot.SegmentTimingOverrides);
    }
}
```

`SessionRevision` is incremented by `MarkSessionInputsChanged`, which is called from:
- Any speaker voice assignment change (user edits the speaker panel)
- Any segment timing override change
- Any speaker reference path update
- Any pipeline reset
- Session restore

Every one of these also changes specific session fields that are ALREADY compared individually
by the three `DictionariesEqual` calls below. The `SessionRevision` check is therefore redundant
for detecting TTS-relevant mutations, but it fires on ANY session mutation — including mutations
the user makes innocuously while waiting for a long Qwen run.

When `TtsInputsStillMatch` returns `false`, `CommitTtsSessionStateAsync` returns early:

```csharp
// Services/SessionWorkflowCoordinator.Pipeline.cs
if (!TtsInputsStillMatch(snapshot))
{
    _log.Warning(
        $"Discarding TTS run {snapshot.RunId} because session inputs changed. Keeping orphaned artifacts under {snapshot.SegmentsDir}.");
    return;   // <-- silent return, no exception, stage stays at Translated
}
```

The orchestrator (`TtsPipelineOrchestrator.ExecuteAsync`) returns normally. The coordinator's
pipeline stage stays at `Translated`. From the UI: TTS ran, nothing changed. Classic stall.

**This is provider-agnostic — it affects all TTS providers equally.**

---

### 2. `_audioProcessingService is null` causes all Qwen segments to fail silently

**File:** `Services/SessionWorkflowCoordinator.TtsReference.cs`

```csharp
if (!File.Exists(outputPath))
{
    if (_audioProcessingService is not null)
    {
        await _audioProcessingService.ExtractAudioClipAsync(...);
    }
    else
    {
        _log.Warning("Audio processing service unavailable. Qwen auto reference extraction skipped.");
        return;   // <-- returns without setting any reference path on CurrentSession
    }
}
```

If `_audioProcessingService` is null and the reference clip does not already exist on disk,
the method silently returns. `TtsExecutionSnapshot` is then built with empty
`SpeakerReferenceAudioPaths` and `DefaultVoiceFallback = null`.

In `QwenContainerTtsProvider.ResolveBatchReferenceAudioAsync`, no reference path resolves.
The provider throws `InvalidOperationException("Qwen3-TTS requires reference audio...")`.
That exception is caught inside `GenerateSegmentClipsAsync`:

```csharp
// Services/SessionWorkflowCoordinator.Pipeline.cs  
catch (Exception ex)
{
    _log.Error($"Segment TTS generation failed for {id}: {ex.Message}", ex);
}
```

All segments fail silently. Zero segments → `CommitTtsSessionStateAsync` throws
`InvalidOperationException("TTS stage completed but no segments were generated.")`.

---

### 3. `MaxConcurrency = 1` in `QwenContainerTtsProvider` makes synthesis sequential

**File:** `Services/QwenContainerTtsProvider.cs`

```csharp
// MaxConcurrency is kept at 1 because _referenceIdCache and _autoExtractedReferencePath
// are not thread-safe. Increase only after adding proper synchronization.
public int MaxConcurrency => 1;
```

The `GenerateSegmentsAsync` method is a sequential `foreach` loop. With 20 segments × 30-60s
per segment on GPU = 10-20 minutes of apparent "stall" while synthesis is actually running.
The cause of the non-thread-safety is `Dictionary<string, string> _referenceIdCache` and
`string? _autoExtractedReferencePath`, both of which are mutated from multiple async paths.

---

### 4. Translation channel backpressure stalls the translation stage visually

**File:** `Services/SessionWorkflowCoordinator.Orchestrators.Streaming.cs`

```csharp
var translationChannel = Channel.CreateBounded<TranslationChannelItem>(new BoundedChannelOptions(8)
{
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait,
});
```

Translation produces segments at ~2-5s each. TTS consumes at ~30-60s each (Qwen, MaxConcurrency=1).
After 8 translated segments, `translationWriter.WriteAsync(...)` in `RunStreamingTranslationStageAsync`
blocks. The translation stage appears frozen even though translation itself is done.
The UI shows translation as "in progress" when it's actually waiting for TTS to drain.

---

### 5. Per-segment failures in streaming path are swallowed without count

**File:** `Services/SessionWorkflowCoordinator.Orchestrators.Streaming.cs`

```csharp
private async Task GenerateStreamingTtsSegmentAsync(...)
{
    try
    {
        var result = await task.ConfigureAwait(false);
        if (result.Success && File.Exists(segmentAudioPath))
        {
            await resultWriter.WriteAsync(...);  // only written on success
        }
        else
        {
            _c.Log.Warning($"Streaming TTS failed or file missing for segment {id}.");
        }
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        _c.Log.Error($"Streaming TTS generation failed for {id}: {ex.Message}", ex);
        // swallowed — resultWriter never gets an item for this segment
    }
}
```

`CollectStreamingTtsResultsAsync` only counts items written to `resultWriter`. It cannot
distinguish a failed segment from a not-yet-written segment. If all segments fail, the
stitch gets an empty map, and `CommitTtsSessionStateAsync` throws "zero segments" —
but only after waiting for the entire pipeline to drain.

---

### 6. Blocking async disposal in `ProviderLeaseManager` — latent deadlock

**File:** `Services/ProviderLease.cs`

```csharp
private void DisposeEntry(Entry entry, string reason)
{
    switch (entry.Provider)
    {
        case IAsyncDisposable asyncDisposable:
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();  // blocks
            break;
    }
}
```

`QwenContainerTtsProvider` is `IAsyncDisposable`. Its `DisposeAsync` calls `ResetSessionAsync`
which calls `_extractor.DeleteAsync()`. `DisposeEntry` is called from `RetireCurrent` which
can be triggered from `RetireTtsProviderCache` which can be called from settings-change
handlers. If any of these run on a thread with an active `SynchronizationContext` (Avalonia
UI dispatcher), `GetAwaiter().GetResult()` deadlocks — the continuation needs the same thread
that is blocked waiting for it.

---

## Fixes — Implement in This Order

---

### Fix 1 — Remove `SessionRevision` from `TtsInputsStillMatch`; surface discard as an error

**File:** `Services/SessionWorkflowCoordinator.ExecutionSnapshots.cs`

Remove the `SessionRevision == snapshot.SessionRevision` line from `TtsInputsStillMatch`.
Add explicit checks for the two session fields that are captured in `TtsExecutionSnapshot`
but are NOT currently compared by the field-level checks: `AmbianceAudioPath` and
`DefaultTtsVoiceFallback`.

The replacement check:

```csharp
private bool TtsInputsStillMatch(TtsExecutionSnapshot snapshot)
{
    lock (_sessionLock)
    {
        return CurrentSession.SessionId == snapshot.SessionId
            && snapshot.TranslationIdentity.Matches(CurrentSession.TranslationPath)
            && string.Equals(
                CurrentSession.AmbianceAudioPath,
                snapshot.AmbianceAudioPath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                CurrentSession.DefaultTtsVoiceFallback,
                snapshot.DefaultVoiceFallback,
                StringComparison.Ordinal)
            && DictionariesEqual(CurrentSession.SpeakerVoiceAssignments, snapshot.SpeakerVoiceAssignments)
            && DictionariesEqual(CurrentSession.SpeakerReferenceAudioPaths, snapshot.SpeakerReferenceAudioPaths)
            && DictionariesEqual(CurrentSession.SegmentTimingModeOverrides, snapshot.SegmentTimingOverrides);
    }
}
```

Note: check `WorkflowSessionSnapshot` for the exact property name for `DefaultTtsVoiceFallback`
before writing the code — it may differ slightly. Do not guess.

**File:** `Services/SessionWorkflowCoordinator.Pipeline.cs`

In `CommitTtsSessionStateAsync`, change the silent discard to a thrown exception:

```csharp
if (!TtsInputsStillMatch(snapshot))
{
    _log.Warning(
        $"Discarding TTS run {snapshot.RunId} because session inputs changed. Keeping orphaned artifacts under {snapshot.SegmentsDir}.");
    throw new InvalidOperationException(
        "TTS inputs changed while the pipeline was running. Rerun TTS to apply the latest settings.");
}
```

This surfaces the discard as a visible pipeline error instead of a silent non-completion.
The orchestrator will propagate it to the ViewModel, which should display it as a stage error
(not a crash). Verify that the ViewModel's error handling path shows a user-readable message
for `InvalidOperationException` from the TTS stage.

**Verification:** Start TTS, then immediately change a speaker voice assignment in the speaker
panel. Expect: a visible error message in the TTS stage area, not a silent reset to `Translated`.

---

### Fix 2 — Surface null `_audioProcessingService` as a blocking error for Qwen

**File:** `Services/SessionWorkflowCoordinator.TtsReference.cs`

In `EnsureSingleSpeakerQwenReferenceClipAsync`, replace the silent `return` with a throw:

```csharp
if (!File.Exists(outputPath))
{
    if (_audioProcessingService is not null)
    {
        await _audioProcessingService.ExtractAudioClipAsync(
            mediaPath,
            outputPath,
            startTimeSeconds: 0,
            durationSeconds: 30,
            cancellationToken);
    }
    else
    {
        throw new PipelineProviderException(
            "Qwen3-TTS requires a speaker reference audio clip, but the audio processing " +
            "service is not available. Ensure the audio processing service is registered at startup.");
    }
}
```

`EnsureMultiSpeakerReferenceClipsAsync` already gracefully falls back (it is non-critical
as long as the single-speaker default reference exists). Do not change its null guard.

**Verification:** If you can reproduce the `_audioProcessingService is null` condition (check
`SessionWorkflowCoordinator.cs` constructor for how it's injected), confirm the error message
surfaces in the TTS stage status area. If it is always non-null in practice, this fix still
eliminates a silent failure path that could appear in edge cases (DI misconfiguration, test
harnesses).

---

### Fix 3 — Thread-safe `QwenContainerTtsProvider` and raise `MaxConcurrency`

**File:** `Services/QwenContainerTtsProvider.cs`

This is the largest change. Do it carefully and test with Qwen before merging.

**Step 3a — make `_referenceIdCache` thread-safe:**

Replace:
```csharp
private readonly Dictionary<string, string> _referenceIdCache = new(StringComparer.Ordinal);
```

With:
```csharp
private readonly ConcurrentDictionary<string, string> _referenceIdCache = new(StringComparer.Ordinal);
```

In `EnsureReferenceRegisteredAsync`, replace the check-then-set pattern with an atomic
"get or add via async factory" pattern. Because `ConcurrentDictionary` doesn't natively support
async value factories, use a `SemaphoreSlim(1)` per key pattern or a simple lock:

```csharp
private readonly SemaphoreSlim _referenceRegisterLock = new(1, 1);

private async Task<string?> EnsureReferenceRegisteredAsync(
    string referenceAudioPath,
    string speakerId,
    CancellationToken ct)
{
    var cacheKey = $"{speakerId}|{referenceAudioPath}";
    if (_referenceIdCache.TryGetValue(cacheKey, out var cached))
        return cached;

    await _referenceRegisterLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        // Double-check after acquiring lock
        if (_referenceIdCache.TryGetValue(cacheKey, out cached))
            return cached;

        var refId = await _client.RegisterQwenReferenceAsync(speakerId, referenceAudioPath, ct);
        _referenceIdCache[cacheKey] = refId;
        _log.Debug($"[QwenContainerTts] Registered reference for speaker '{speakerId}': {refId}");
        return refId;
    }
    finally
    {
        _referenceRegisterLock.Release();
    }
}
```

Also dispose `_referenceRegisterLock` in `DisposeAsync`.

**Step 3b — make `_autoExtractedReferencePath` thread-safe:**

Replace:
```csharp
private string? _autoExtractedReferencePath;
```

With a `SemaphoreSlim` guard on `EnsureAutoExtractedReferenceAsync`:

```csharp
private string? _autoExtractedReferencePath;
private readonly SemaphoreSlim _autoExtractLock = new(1, 1);

private async Task<string?> EnsureAutoExtractedReferenceAsync(string sourceVideoPath, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(_autoExtractedReferencePath))
        return _autoExtractedReferencePath;

    await _autoExtractLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        if (!string.IsNullOrWhiteSpace(_autoExtractedReferencePath))
            return _autoExtractedReferencePath;

        _log.Debug($"[QwenContainerTts] Auto-extracting reference audio from: {sourceVideoPath}");
        _autoExtractedReferencePath = await _extractor.ExtractReferenceAsync(sourceVideoPath, ct);
        return _autoExtractedReferencePath;
    }
    finally
    {
        _autoExtractLock.Release();
    }
}
```

Also dispose `_autoExtractLock` in `DisposeAsync`.

**Step 3c — raise MaxConcurrency:**

```csharp
public int MaxConcurrency => 2;
```

Start with 2. The Python server's `_qwen_segment_semaphore` already controls GPU access
correctly. Two concurrent C# requests will queue on the Python side if needed.

**Step 3d — parallelize `GenerateSegmentsAsync`:**

Replace the sequential `foreach` in `GenerateSegmentsAsync` with `Parallel.ForEachAsync`:

```csharp
public async Task<IReadOnlyDictionary<string, string>> GenerateSegmentsAsync(
    IReadOnlyList<QwenBatchSegmentRequest> requests,
    IProgress<(int Completed, int Total)>? progress = null,
    CancellationToken cancellationToken = default)
{
    if (requests.Count == 0)
        return new Dictionary<string, string>(StringComparer.Ordinal);

    var outputPaths = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    var completed = 0;

    await Parallel.ForEachAsync(
        requests,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrency,
            CancellationToken = cancellationToken,
        },
        async (request, ct) =>
        {
            var referenceAudioPath = await ResolveBatchReferenceAudioAsync(request, ct);
            if (string.IsNullOrWhiteSpace(referenceAudioPath))
                throw new InvalidOperationException(
                    $"Qwen3-TTS requires reference audio for segment '{request.SegmentId}'.");

            var speakerId = request.SpeakerId ?? QwenReferenceKeys.SingleSpeakerDefault;
            var model = ResolveModel(request.VoiceName);
            _log.Debug($"[QwenContainerTts] Segment synth start ({Interlocked.Increment(ref completed)}/{requests.Count}): {request.SegmentId}");

            var result = await QwenSegmentWithRetryAsync(
                request.Text,
                model,
                ResolveLanguage(request.Language),
                referenceAudioPath,
                speakerId,
                referenceText: null,
                ct);

            if (!result.Success)
                throw new InvalidOperationException(
                    $"Qwen synthesis failed for segment '{request.SegmentId}': {result.ErrorMessage}");

            await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, ct);
            outputPaths[request.SegmentId] = request.OutputAudioPath;
            _log.Debug($"[QwenContainerTts] Segment synth saved: {request.OutputAudioPath}");
            progress?.Report((outputPaths.Count, requests.Count));
        }).ConfigureAwait(false);

    if (outputPaths.Count != requests.Count)
        throw new InvalidOperationException("Qwen batch synthesis did not return every requested segment.");

    return outputPaths;
}
```

Note: `completed` counter for logging can be `int` with `Interlocked.Increment` since it's
just for the log message. The `progress` report uses `outputPaths.Count` which is thread-safe
on `ConcurrentDictionary`.

**Verification:** Run a multi-segment video with Qwen. Check log timestamps — segments should
now start within seconds of each other rather than each waiting for the previous to finish.

---

### Fix 4 — Fix blocking async disposal in `ProviderLeaseManager`

**File:** `Services/ProviderLease.cs`

```csharp
private void DisposeEntry(Entry entry, string reason)
{
    try
    {
        switch (entry.Provider)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case IAsyncDisposable asyncDisposable:
                // Run on thread pool to avoid deadlock if called from a SynchronizationContext
                // (e.g., Avalonia UI dispatcher thread).
                Task.Run(() => asyncDisposable.DisposeAsync().AsTask())
                    .GetAwaiter()
                    .GetResult();
                break;
        }
    }
    catch (Exception ex)
    {
        _log.Warning(
            $"Retired {_providerLabel} provider '{entry.ProviderId}' generation {entry.CacheGeneration} disposal failed after {reason}: {ex.Message}");
    }
}
```

**Verification:** Trigger a settings change (switch TTS provider) while on the Avalonia UI thread
and confirm the app does not deadlock or hang. Check that no warning is logged from the disposal path.

---

### Fix 5 — Unbounded translation channel in streaming path

**File:** `Services/SessionWorkflowCoordinator.Orchestrators.Streaming.cs`

There are two places where `translationChannel` is created — one in `ExecuteFullPipelineAsync`
(lines ~105) and one in `ExecuteTranslationAndTtsFromTranscriptAsync` (lines ~367). Change both.

Replace:
```csharp
var translationChannel = Channel.CreateBounded<TranslationChannelItem>(new BoundedChannelOptions(8)
{
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait,
});
```

With:
```csharp
var translationChannel = Channel.CreateUnbounded<TranslationChannelItem>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = true,
});
```

Translation segments are small JSON payloads. There is no OOM risk from buffering a full
translation result set before TTS consumes it. This lets the translation stage complete and
report progress correctly even when Qwen is slow.

**Verification:** In the streaming pipeline, after translation finishes on a multi-segment video,
the translation stage UI should immediately report "Translation complete" rather than appearing
blocked. TTS should continue running independently.

---

### Fix 6 — Surface per-segment failures in streaming TTS

**File:** `Services/SessionWorkflowCoordinator.Orchestrators.Streaming.cs`

In `GenerateStreamingTtsSegmentAsync`, always write a `TtsChannelItem` regardless of success:

```csharp
private async Task GenerateStreamingTtsSegmentAsync(
    TtsExecutionSnapshot snapshot,
    TranslationChannelItem item,
    ChannelWriter<TtsChannelItem> resultWriter,
    CancellationToken cancellationToken)
{
    var id = item.SegmentId;
    var text = item.Segment.TranslatedText;
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
        return;

    var segmentAudioPath = Path.Combine(snapshot.SegmentsDir, $"{id}.mp3");
    var resolvedVoice = snapshot.ResolveVoiceForSegment(item.Segment);
    var referenceAudioPath = snapshot.ResolveReferenceAudioForSegment(item.Segment);

    TtsResult result;
    try
    {
        var task = _c._inferenceEngine.GenerateSegmentTtsAsync(
            snapshot.Provider,
            new SingleSegmentTtsRequest(
                text,
                segmentAudioPath,
                resolvedVoice,
                item.Segment.SpeakerId,
                referenceAudioPath,
                Language: snapshot.Language,
                SourceVideoPath: snapshot.SourceVideoPath),
            cancellationToken);
        _c.TrackPendingTtsTask(task);
        result = await task.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        _c.Log.Error($"Streaming TTS generation failed for {id}: {ex.Message}", ex);
        result = new TtsResult(false, null, resolvedVoice, 0, ex.Message);
    }

    // Always write — collector needs to count both successes and failures.
    var audioPathForResult = result.Success && File.Exists(segmentAudioPath) ? segmentAudioPath : null;
    if (!result.Success || audioPathForResult is null)
        _c.Log.Warning($"Streaming TTS failed or file missing for segment {id}: {result.ErrorMessage}");

    await resultWriter.WriteAsync(
        new TtsChannelItem(
            id,
            CloneTranslationSegment(item.Segment),
            result with { AudioPath = audioPathForResult }),
        cancellationToken).ConfigureAwait(false);
}
```

In `CollectStreamingTtsResultsAsync`, track failures explicitly:

```csharp
private async Task<ConcurrentDictionary<string, string>> CollectStreamingTtsResultsAsync(
    ChannelReader<TtsChannelItem> resultReader,
    PipelineStageContext? stageContext,
    CancellationToken cancellationToken)
{
    var segmentAudioPaths = new ConcurrentDictionary<string, string>();
    var completed = 0;
    var failed = 0;

    await foreach (var item in resultReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
    {
        completed++;
        if (item.Result.Success && !string.IsNullOrWhiteSpace(item.Result.AudioPath) && File.Exists(item.Result.AudioPath))
        {
            segmentAudioPaths[item.SegmentId] = item.Result.AudioPath;
        }
        else
        {
            failed++;
        }

        var statusMsg = failed > 0
            ? $"Generated {completed} segment clips ({failed} failed)…"
            : $"Generated segment clip {completed}…";

        ReportStage(
            stageContext,
            statusMsg,
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Translation is still feeding new segments downstream.");
    }

    return segmentAudioPaths;
}
```

**Verification:** Force a segment failure (provide a malformed reference audio path to one
segment). Confirm the stage context message shows "(1 failed)" rather than silently succeeding
with a partial result.

---

## Files to Change — Summary

| File | Fixes |
|------|-------|
| `Services/SessionWorkflowCoordinator.ExecutionSnapshots.cs` | Fix 1: remove `SessionRevision` check, add `AmbianceAudioPath` + `DefaultVoiceFallback` checks |
| `Services/SessionWorkflowCoordinator.Pipeline.cs` | Fix 1: change silent discard to thrown `InvalidOperationException` |
| `Services/SessionWorkflowCoordinator.TtsReference.cs` | Fix 2: null `_audioProcessingService` throws instead of silently returning |
| `Services/QwenContainerTtsProvider.cs` | Fix 3: thread-safe cache + auto-extract, raise `MaxConcurrency` to 2, parallelize `GenerateSegmentsAsync` |
| `Services/ProviderLease.cs` | Fix 4: wrap async disposal in `Task.Run` to avoid SynchronizationContext deadlock |
| `Services/SessionWorkflowCoordinator.Orchestrators.Streaming.cs` | Fix 5: unbounded `translationChannel`; Fix 6: always write `TtsChannelItem`, track failures in collector |

---

## What Not to Do

- Do not add `SessionRevision` checks anywhere else. The field-level checks are sufficient.
- Do not change the `ttsResultChannel` bounds — TTS result backpressure is intentional.
- Do not change `MaxConcurrency` above 2 without benchmarking on the target GPU. The Python
  `_qwen_segment_semaphore` will queue requests correctly but VRAM is a real constraint.
- Do not add a `MaxConcurrency` setting to `AppSettings` in this pass — hard-code to 2 for now
  and profile before exposing it to users.
- Do not change `TtsSettingsDrifted` — it is used to mark artifact staleness for UX warnings
  (the "settings changed since last run" badge), not for pipeline invalidation.
- Do not modify the Python `inference/main.py` server — the C# changes are sufficient.

---

## Verification Checklist

Run these in order. Do not mark any item done unless it has been confirmed with an actual run,
not just a code review.

- [ ] Fix 1: Touch a speaker voice assignment while TTS is running (Qwen or Edge TTS). Confirm
      a user-visible error appears ("TTS inputs changed…"), not a silent stage reset.
- [ ] Fix 2: Verify `_audioProcessingService` registration in DI root. If null is possible in
      any valid startup path, confirm the `PipelineProviderException` surfaces in the stage context.
- [ ] Fix 3: Run Qwen TTS on a video with 10+ segments. Confirm log shows multiple segment synths
      starting within a few seconds of each other (not strictly sequential). Confirm no race
      condition in reference registration (same reference ID for same speaker across concurrent calls).
- [ ] Fix 4: Change TTS provider in Settings while the app is on the Avalonia UI thread. Confirm
      no hang or deadlock. Check for disposal warning in logs.
- [ ] Fix 5: Run the full streaming pipeline (transcription → translation → TTS) with Qwen.
      Confirm the translation stage reports "Translation complete" before TTS finishes.
- [ ] Fix 6: Induce a segment failure (e.g., pass a zero-byte reference audio file). Confirm
      the TTS stage status shows the failure count, not a silent zero-segment error.
