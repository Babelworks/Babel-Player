# Architectural Blueprint: ReactiveUI and Zafiro Presentation Layer

> **Status:** Draft / Feasibility Study (not adopted)
> **Written:** 2026-09-09
> **Target:** v1.4+ Settings density and custom timeline canvas
> **Core tenet compliance:** Subordinate to `docs/architecture.md` and `docs/babel-2.0-tenets.md`. This file does not authorize package adds or ViewModel rewrites.

This document is a field study of whether ReactiveUI (with `ReactiveUI.SourceGenerators`) and `Zafiro.Avalonia` should enter the presentation layer. It is not current architecture. `docs/architecture.md` remains the structural source of truth. Do not treat the blueprints below as permission to introduce a second MVVM stack.

## Executive verdict

| Question | Verdict | Single biggest risk |
|---|---|---|
| Use `System.Reactive` for high-frequency UI streams | **Go** (already in repo) | Putting `.Sample()` / `.Throttle()` on `TaskPoolScheduler` for pointer events, then updating Skia from the wrong thread |
| Add `ReactiveUI.SourceGenerators` on **new** `ReactiveObject` ViewModels only | **Go** (isolated classes; no retrofits) | `[Reactive]` cannot share a class with CommunityToolkit `[ObservableProperty]`. All current VMs derive `ViewModelBase : ObservableObject` |
| Add `ReactiveUI.Avalonia` (`WhenActivated`, `ReactiveWindow`) | **Conditional Go** | Latest 12.1.2 wants Avalonia **>= 12.1.2**. Stay on **12.0.3** while this repo pins Avalonia 12.0.1. `UseReactiveUI()` brings Splat and must never be required for `--dub` |
| Add `Zafiro.Avalonia` for Settings / commands / wizards | **Conditional Go later; No-Go for Milestone A** | 53.3.1 floors Avalonia **12.0.4**, pins ReactiveUI 23.2.28 / `ReactiveUI.Avalonia` 11.4.13, and pulls CSharpFunctionalExtensions, ReactiveProperty, Serilog, and Xaml.Behaviors. Single maintainer (SuperJMN). Avalonia 12 support itself is real (upstream migration commits) |
| Replace CommunityToolkit ViewModels wholesale | **No-Go** | `SettingsViewModel` and `EmbeddedPlaybackViewModel` are already toolkit-generated; a rewrite does not serve the product chain |

Milestone A in `docs/agent-handoff.md` (Settings checkbox for `ChatterboxVoiceCloneConsent`) does not require either toolkit. The live two-way checkbox already exists on the main window. Settings can host a duplicate draft toggle with the existing `[ObservableProperty]` + `Apply()` path.

`IEnhancedCommand` (verified in Zafiro.UI) extends `IReactiveCommand` with `IsExecuting`, `CanExecute`, `Name`, and `Text`. Result-typed overloads (`.AsResult()`, `CreateWithResult`) require CSharpFunctionalExtensions in the ViewModel adapter. There is still no progress-float or status-string channel on the command itself. Percent-complete lives in Zafiro's separate Jobs/Actions types (`IJob` / `IExecution` / `LongProgress`) or, as we already do, in `IProgress<PipelineStageUpdate>` on `EmbeddedPlaybackPipelineViewModel`. Zafiro cannot delete that tracking.

---

## 1. Architectural Topology and Layer Boundaries

### 1.1 Actual assembly shape (verify this first)

Babel Player is a **single WinExe**: `BabelPlayer.csproj` compiles `Services/`, `ViewModels/`, `Views/`, and `Program.cs` into one assembly. Headless is not a separate project. `Program.Main` intercepts `--dub`, `--tui`, and `--benchmark` and returns before `BuildAvaloniaApp()`.

```text
BabelPlayer.exe
  |
  +-- --dub / --tui / --benchmark  ->  DubCli / DubTui / BenchmarkCli
  |                                      (no Avalonia lifetime)
  |
  +-- default desktop lifetime     ->  AppBuilder -> Views / ViewModels
                                         coordinator still in-process
```

The seam is therefore **source-level**, not compile-time:

- Headless drivers and inference workers must not construct ReactiveUI views, `WhenActivated` scopes, Zafiro shells, or Avalonia dispatchers.
- Adding `ReactiveUI.Avalonia` or `Zafiro.Avalonia` still copies those assemblies next to `BabelPlayer.exe`. `--dub` will load the same WinExe. That is acceptable only if those types are never touched on the headless path.
- Tenet 8 (`docs/babel-2.0-tenets.md`) wants compiled architecture tests for dependency direction. A future split (presentation project vs headless core) would make the seam real. Do not pretend the split exists today.

### 1.2 Data and control flow topology map

```text
+-------------------------------------------------------------------------+
| PRESENTATION LAYER  (Views/, ViewModels/)                               |
|                                                                         |
|  [ Avalonia XAML Views / future Skia canvas ]                           |
|           ^  compiled bindings, pointer events                          |
|           |                                                             |
|  [ CommunityToolkit ViewModels  |  optional ReactiveObject islands ]    |
|           |  RelayCommand / IProgress adapters                          |
|           |  existing System.Reactive subscriptions (Throttle)          |
+-------------------------------------------------------------------------+
            |  observes Task/IProgress/IObservable seams only
            v
+-------------------------------------------------------------------------+
| HEADLESS CORE  (Services/, Models/)                                     |
|                                                                         |
|  [ SessionWorkflowCoordinator ]                                         |
|      owns workflow state, artifacts, settings persistence               |
|      already: ObservableObject + Subject<ReadinessSignal>               |
|      pipeline progress: IProgress<PipelineStageUpdate>                  |
|                                                                         |
|  [ Providers / ONNX / unmanaged buffers ]                               |
|      Task + CancellationToken + domain records                          |
|      (TranscriptionResult, TranslationResult, TtsResult, ...)           |
+-------------------------------------------------------------------------+
```

Hard rule: ReactiveUI types, Zafiro types, Avalonia types, and `Dispatcher.UIThread` stay above the line. The coordinator may keep the existing `IObservable<ReadinessSignal>` seam. Do not add `ReactiveCommand`, `WhenActivated`, `IEnhancedCommand`, or `CSharpFunctionalExtensions.Result` to `Services/` or `DubCli.cs`.

### 1.3 What is already true in this repo

These facts override the handoff where they disagree:

1. **MVVM is CommunityToolkit.Mvvm 8.4.2.** `ViewModelBase` inherits `ObservableObject`. Settings, playback, pipeline, and the speaker wizard all use `[ObservableProperty]` / `[RelayCommand]`.
2. **`System.Reactive` 6.1.0 is already referenced.** `SettingsViewModel` and `EmbeddedPlaybackViewModel` throttle `coordinator.ReadinessSignals`. The coordinator itself constructs `Subject<ReadinessSignal>` (`Services/SessionWorkflowCoordinator.cs`).
3. **There is no shared functional `Result<T>`.** Stage contracts in `Services/StageContracts.cs` are domain records with `Success` and `ErrorMessage`. Mapping those to CSharpFunctionalExtensions is a ViewModel adapter, not a core change.
4. **`ChatterboxVoiceCloneConsent` already has a GUI toggle.** `Views/MainWindow.axaml` binds `Playback.ChatterboxVoiceCloneConsent` two-way and hides it unless Chatterbox is selected. `BabelPlayer.Tests/ChatterboxConsentTests.cs` asserts that binding. `Views/SettingsWindow.axaml` does not mention consent. CLI grant is `--consent-clone` and is not persisted (`docs/headless-cli.md`).
5. **Pipeline status and progress already exist** on `EmbeddedPlaybackPipelineViewModel` (`PipelineStageTitle`, `PipelineStageDetail`, `PipelineProgressPercent`) fed by `IProgress<SessionWorkflowCoordinator.PipelineStageUpdate>`.
6. **No Skia timeline canvas exists.** No `WhenActivated`, `IActivatableViewModel`, or `[ObservableAsProperty]` usages exist.
7. **A wizard already exists:** `SpeakerReferenceWizardViewModel` (CommunityToolkit, `IDisposable`). Product-chain progression (ingest -> transcribe -> translate -> TTS) is owned by `SessionWorkflowCoordinator`, not by a UI wizard framework (`docs/architecture.md` state ownership).

### 1.4 Constraint verification checklist

| Constraint | Current state |
|---|---|
| No `ReactiveUI`, `ReactiveUI.Avalonia`, or `Zafiro` references in `Services/` | **Pass.** Those packages are not in `BabelPlayer.csproj`. |
| No `System.Reactive` types in `Services/` | **Already leaked.** `SessionWorkflowCoordinator` uses `Subject<ReadinessSignal>` and exposes `IObservable<ReadinessSignal>`. Grandfather this as the allowed core->shell signal seam. Do not add `System.Reactive.Linq` operators, `ReactiveCommand`, or schedulers into Services. |
| `DubCli.cs` compiles without presentation types | **Pass at source.** `--dub` never calls `BuildAvaloniaApp()`. Keep it that way. Package adds do not create a second assembly. |
| `BabelPlayer.Tests` stays zero-sleep / no scheduler clock pauses | **Pass today.** Future Rx tests must use `TestScheduler.With(...)` (virtual time) or stay out of the compiled suite. Do not `Thread.Sleep` to wait on `Throttle`/`Sample`. See `docs/testing-requirements.md`. |
| Coordinator remains the workflow owner | **Pass.** Do not move stage sequencing into SlimWizard / Zafiro Shell. |

---

## 2. Zafiro Functional Pipeline Integration

### 2.1 Mapping domain results to `IEnhancedCommand`

**What Zafiro actually provides (verified 2026-09-09):**

- Package: `Zafiro.Avalonia` **53.3.1** (NuGet, MIT, `net10.0` only).
- README still labels the library "Avalonia 11.3.x", but the 53.3.1 nuspec floors `Avalonia (>= 12.0.4)` and `Avalonia.Skia (>= 12.0.4)`. Upstream did migrate (commits `c3f13ed`, `4207976`).
- Built on ReactiveUI 23.2.28 plus CSharpFunctionalExtensions `Result` / `Maybe`.
- `IEnhancedCommand` is a `ReactiveCommand` wrapper: `IsExecuting`, `CanExecute`, `Name`, `Text`, plus `.Enhance(...)`. Result helpers (`.AsResult()`) exist. That is busy-state and error-monad UX, not a progress bus.
- `Zafiro.Avalonia.Dialogs` 53.3.1 pulls `ReactiveUI.SourceGenerators` **2.6.1**, older than 3.2.0. If both are referenced, verify analyzer resolution before shipping.
- SlimWizard (`WizardBuilder.StartWith(...)`) exists for modal multi-step dialogs.
- GraphWizard appears in 53.x changelog (fresh VM per step). That is navigation chrome, not an inference orchestrator.

**What Zafiro 53.3.1 also pulls (this is why Milestone A stays CommunityToolkit):**

| Transitive / direct | Floor on 53.3.1 | Conflict with Babel Player |
|---|---|---|
| Avalonia | >= 12.0.4 | We pin **12.0.1** in `BabelPlayer.csproj` |
| `ReactiveUI.Avalonia` | 11.4.13 (Zafiro pin; compiled against Avalonia 11.3.17) | Latest 12.1.2 wants Avalonia **>= 12.1.2**. Mixing Zafiro's 11.4.13 under Avalonia 12.0.4 is what Zafiro ships; it still needs an app-level smoke pass. An unpinned restore of 12.1.x is worse |
| `ReactiveUI` | 23.2.28 (Zafiro pin) | Second MVVM stack. ReactiveUI 24.x splits lean `ReactiveUI` (no System.Reactive) vs `ReactiveUI.Reactive`. If we ever leave 23.2.28, take `ReactiveUI.Reactive` because this repo already depends on System.Reactive 6.1.0 |
| `ReactiveProperty` | >= 9.8.0 | Third INPC/Rx library |
| `CSharpFunctionalExtensions` | >= 3.6.0 | New error type, not used in core |
| `Serilog` | >= 4.3.0 | Parallel to `AppLog` |
| `Xaml.Behaviors.Avalonia` and drag-drop packages | >= 12.0.0 | New behavior stack |
| `Zafiro.UI` | >= 47.1.1 | Functional UI primitives |

`docs/architecture.md` prefers narrow service seams over giant early abstraction layers. Taking Zafiro to host one Settings checkbox, or to wrap a command that already has `IProgress`, violates that bias.

**Honest mapping of our results:**

```csharp
// Domain record in Services/StageContracts.cs (do not change this to CFE Result)
public sealed record TranscriptionResult(
    bool Success,
    IReadOnlyList<TranscriptSegment> Segments,
    string Language,
    double LanguageProbability,
    string? ErrorMessage,
    long ElapsedMs = 0,
    ...);
```

A ViewModel adapter may convert at the shell boundary:

```csharp
using CSharpFunctionalExtensions; // only if Zafiro is adopted; not present today

static Result<TranscriptionResult> ToUiResult(TranscriptionResult r) =>
    r.Success ? Result.Success(r) : Result.Failure<TranscriptionResult>(r.ErrorMessage ?? "Transcription failed");
```

That adapter must live in `ViewModels/`, never in `Services/`. Headless CLI already consumes the domain records directly.

**`IEnhancedCommand` cannot replace progress properties.** The current pipeline command already reports title, detail, percent, and indeterminate state:

```89:126:ViewModels/EmbeddedPlaybackPipelineViewModel.cs
    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    public async Task RunPipelineAsync()
    {
        // ...
        var stageProgress = new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate);
        await _coordinator.AdvancePipelineAsync(
            progress: null,
            stageProgress: stageProgress,
            cancellationToken: cancellationToken);
```

A Zafiro-shaped wrap would still bind those fields. Blueprint (illustration only; do not add Zafiro to do this):

```csharp
// ViewModels/ only. Coordinator call stays Task + IProgress + CancellationToken.
public sealed partial class PipelineRunHost : ReactiveObject
{
    public PipelineRunHost(SessionWorkflowCoordinator coordinator)
    {
        var run = ReactiveCommand.CreateFromTask(async ct =>
        {
            var progress = new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(u =>
            {
                StageTitle = u.Title;          // still a property
                StatusText = u.Detail;         // still a property
                Progress01 = u.Progress01;     // still a property
            });
            await coordinator.AdvancePipelineAsync(
                progress: null,
                stageProgress: progress,
                cancellationToken: ct);
        });

        RunPipeline = run.Enhance("Run pipeline", name: "run-pipeline");
        // RunPipeline.IsExecuting -> spinner; distinct from CanExecute
        // Optional: run.AsResult() maps CFE Result, not PipelineStageUpdate
        // ThrownExceptions -> map to StatusText; do not throw across ONNX
    }

    public IEnhancedCommand RunPipeline { get; }
    [Reactive] private string _stageTitle = "Ready";
    [Reactive] private string _statusText = "";
    [Reactive] private double _progress01;
}
```

That is more types, not less boilerplate. Keep `[RelayCommand]` + `IProgress` for pipeline runs unless a later milestone proves ReactiveCommand's `IsExecuting` is worth a dual stack.

### 2.2 Wizard navigation for multi-stage dubbing

Do **not** migrate Media -> Whisper -> Translate -> TTS into SlimWizard.

`docs/architecture.md` assigns that sequence to the session/workflow coordinator. A UI wizard that owns stage order would fragment state across views, which the architecture explicitly rejects.

| Flow | Owner today | Zafiro SlimWizard fit |
|---|---|---|
| Ingest -> transcribe -> translate -> TTS -> preview | `SessionWorkflowCoordinator` | Poor. Long-running, resumable, artifact-backed. Not a modal page graph. |
| Speaker reference assignment | `SpeakerReferenceWizardViewModel` | Weak. Already a dedicated window with preview transport. Rewrite cost is high, product gain is low. |
| Voice-clone license / subject consent (if it becomes a multi-page legal gate) | Settings + MainWindow checkbox + CLI flag | Possible later, if consent grows past one boolean. Not needed for the current toggle. |

If a future legal-consent wizard is required (tenet 7: voice-cloning subject consent), isolate it as a modal over `AppSettings.ChatterboxVoiceCloneConsent` and still persist through `SettingsService`. Do not let the wizard call ONNX.

---

## 3. High-Frequency Stream Management and Performance Profiles

### 3.1 Timeline scrubbing and event throttling

Pointer motion is a UI-thread problem. ONNX seek/preview is a background problem. Sampling must not mix those schedulers.

**Wrong (handoff sketch):** `.Sample(16ms)` on `TaskPoolScheduler.Default` for the whole cursor channel. That hops pointer coordinates off the UI thread, then either (a) renders Skia from a thread pool thread, or (b) floods `ObserveOn(MainThread)` with allocations. Neither protects ONNX.

**Right split:**

1. Convert pointer -> time on the UI thread (cheap math).
2. Sample or throttle the *time offset* at ~16 ms on the UI scheduler so Skia invalidation is capped at ~60 Hz.
3. DistinctUntilChanged the seek target so identical frames do not retrigger work.
4. Only the expensive seek/decode/ONNX preview hops to `TaskPoolScheduler` / `Task.Run`, with `Switch()` so a new scrub cancels the previous seek.
5. Marshal *results* (waveform tiles, peek samples) back with `ObserveOn(RxApp.MainThreadScheduler)` or `Dispatcher.UIThread.Post`.

Blueprint for a future canvas (not in repo today). Keep this in `ViewModels/` or a view code-behind that talks to a ViewModel. Do not put it in `Services/Chatterbox/` or ONNX engine types.

```csharp
using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI;

public sealed partial class SegmentTimelineViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();

    public SegmentTimelineViewModel(Control canvas, Func<double, TimeSpan> xToTime, ITimelineAudioPreview preview)
    {
        this.WhenActivated(d =>
        {
            var moves = Observable.FromEventPattern<PointerEventArgs>(
                    h => canvas.PointerMoved += h,
                    h => canvas.PointerMoved -= h)
                .Select(e => xToTime(e.EventArgs.GetPosition(canvas).X));

            var sampled = moves
                .Sample(TimeSpan.FromMilliseconds(16), RxApp.MainThreadScheduler)
                .DistinctUntilChanged();

            // Cheap: invalidate Skia at 60 Hz on the UI thread.
            sampled
                .Subscribe(t => Playhead = t)
                .DisposeWith(d);

            // Expensive: at most one in-flight seek; never queue ONNX sessions.
            sampled
                .Throttle(TimeSpan.FromMilliseconds(16), RxApp.MainThreadScheduler)
                .Select(t => Observable.FromAsync(ct => preview.SeekPreviewAsync(t, ct)))
                .Switch()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(frame => PreviewFrame = frame, ex => PreviewError = ex.Message)
                .DisposeWith(d);
        });
    }

    [Reactive] private TimeSpan _playhead;
    [ObservableAsProperty] private object? _previewFrame;
    [Reactive] private string? _previewError;
}

public interface ITimelineAudioPreview
{
    // Implementation lives behind a service seam. Must bound concurrency.
    Task<object> SeekPreviewAsync(TimeSpan time, CancellationToken ct);
}
```

Operator choice:

| Operator | Use on the timeline |
|---|---|
| `Sample(16ms)` | Emit the latest pointer time every 16 ms while movement continues. Best for a live playhead. |
| `Throttle(16ms)` | Emit after movement pauses for 16 ms. Better for "seek when the user hesitates", worse for dragging. |
| `Buffer` | Rarely useful here; adds latency and list allocations. |

`preview.SeekPreviewAsync` must serialize ONNX access (single session, or a bounded channel). Sampling the UI does not by itself protect unmanaged runtimes if each sample starts a new inference.

Existing precedent in-repo (300 ms, not 16 ms):

```157:161:ViewModels/SettingsViewModel.cs
        _readinessSignalSubscription = _coordinator.ReadinessSignals
            .Select(signal => $"{signal.Kind}:{signal.Source}:{signal.Summary}:{signal.ForceRefresh}")
            .DistinctUntilChanged(StringComparer.Ordinal)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateBackendStatus));
```

Milestone B can follow that pattern with a tighter interval and an explicit dispose scope.

### 3.2 Thread marshalling and scheduler allocation

`Program.BuildAvaloniaApp()` does not call `UseReactiveUI()`. If ReactiveUI.Avalonia is added, `UseReactiveUI()` must run so `RxApp.MainThreadScheduler` is `AvaloniaScheduler.Instance`. Until then, prefer `Dispatcher.UIThread.Post` as Settings already does.

| Operation class | Primary execution | Marshalling / UI notification |
|---|---|---|
| Pointer -> time math, Skia invalidate, binding updates | Avalonia UI thread / `RxApp.MainThreadScheduler` | Native binding loop |
| Timeline window scrolling / playhead | UI thread | Do not hop to TaskPool |
| Audio content-sniff, waveform tile decode | `Task.Run` / `TaskPoolScheduler.Default` | `.ObserveOn(RxApp.MainThreadScheduler)` or `Dispatcher.UIThread.Post` |
| ONNX tensor generation, local Chatterbox / Whisper sessions | Dedicated worker or existing provider `Task` (not a new Rx scheduler per pointer event) | Progress via `IProgress<T>` already used by coordinators; UI observes that |
| Headless `--dub` pipeline | Thread pool via existing `Task` / orchestrators | Console / TUI only. No RxApp, no Dispatcher |

Do not use `RxApp.TaskpoolScheduler` as a substitute for the provider-level concurrency gates already in `ProviderLeaseManager` and ONNX engines.

---

## 4. Memory Management and Garbage Collection Guardrails

ONNX sessions and audio buffers are unmanaged or large managed arrays. A leaked Rx subscription on a long-lived window keeps those graphs alive.

### 4.1 Activation lifecycles (`WhenActivated`)

Current pattern (keep this even without ReactiveUI): ViewModels that subscribe to `ReadinessSignals` implement `IDisposable` and dispose the subscription (`SettingsViewModel.Dispose`, `EmbeddedPlaybackViewModel.Dispose`). Window/code-behind must call `Dispose` on close.

If ReactiveUI views are introduced:

- Views that own subscriptions inherit `ReactiveWindow<T>` / `ReactiveUserControl<T>` and subscribe only inside `WhenActivated`.
- Every `Subscribe`, `Bind`, `BindCommand`, and `ToProperty` call gets `.DisposeWith(disposables)`.
- ViewModels that start streams implement `IActivatableViewModel` and use the same `WhenActivated` so a hidden timeline does not keep sampling.
- Do not subscribe in a constructor without storing `IDisposable` on a field that `Dispose()` releases. Constructor subscriptions outlive tab switches.

Avalonia quirk: activation follows visual attachment. A control that stays in the tree but is `IsVisible=false` may remain activated depending on the activation fetcher. For a dense timeline, dispose explicitly when the pane collapses (same as today's `IDisposable` VMs), do not rely on `WhenActivated` alone.

Do not wrap `SessionWorkflowCoordinator` in `WhenActivated`. The coordinator outlives any one view.

### 4.2 Array pooling inside streams

The repo does not currently use `System.Buffers.ArrayPool`. Future waveform/timeline tiles should.

Rules:

- Rent on the worker that fills the buffer, not on the UI thread.
- Return in a `finally` that runs on both completion and cancellation. Rx: `using` inside `FromAsync`, or `Observable.Create` with teardown, or `.Finally(() => pool.Return(buffer))`.
- Never return a buffer that is still bound into a Skia `SKImage` / `WriteableBitmap`. Copy or transfer ownership first.
- Do not `Return` twice. Clear the local reference after return.
- Cancellation: `Switch()` disposes the previous inner observable; the inner `FromAsync` must honor `CancellationToken` and return rented arrays in `finally`.

```csharp
Observable.Create<byte[]>(observer =>
{
    var buffer = ArrayPool<byte>.Shared.Rent(minLength);
    var cts = new CancellationTokenSource();
    _ = Task.Run(async () =>
    {
        try
        {
            await FillWaveformAsync(buffer, cts.Token).ConfigureAwait(false);
            observer.OnNext(buffer);
            observer.OnCompleted();
        }
        catch (Exception ex)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null!;
            observer.OnError(ex);
        }
    }, cts.Token);

    return Disposable.Create(() =>
    {
        cts.Cancel();
        cts.Dispose();
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    });
});
```

Prefer letting the preview service own pooled buffers and expose an immutable frame object to the VM, so Rx only carries a small handle.

ONNX: ViewModels must not dispose engine sessions. Session lifetime stays in `Services/Chatterbox/` (and future native engines). A cancelled seek cancels work; it does not `session.Dispose()`.

---

## 5. Source generator evaluation (ReactiveUI vs CommunityToolkit)

`ReactiveUI.SourceGenerators` 3.2.0 (NuGet, MIT, `PrivateAssets="all"`) requires C# 12, ReactiveUI 23.2.28+, and supports Roslyn 5.0 / .NET 10 SDK.

Typical reduction:

```csharp
using ReactiveUI;
using ReactiveUI.SourceGenerators;

public partial class TimelineToolsViewModel : ReactiveObject
{
    [Reactive] private bool _snapToSegment;
    [Reactive] private double _zoom;

    [ObservableAsProperty] private string _zoomLabel = "100%";

    [ReactiveCommand]
    private void ZoomIn() => Zoom = Math.Min(Zoom * 1.25, 8);

    public TimelineToolsViewModel()
    {
        this.WhenAnyValue(x => x.Zoom)
            .Select(z => $"{z * 100:0}%")
            .ToProperty(this, nameof(ZoomLabel), out _zoomLabelHelper);
    }
}
```

That is leaner than handwritten `RaiseAndSetIfChanged`. It is **not** leaner than the CommunityToolkit code already in `SettingsViewModel`:

```csharp
[ObservableProperty] private bool _autoSaveEnabled;
[RelayCommand] private void Apply() { ... }
```

**Do not mix `[ObservableProperty]` and `[Reactive]` on the same class.** Two generators emitting partial properties for one VM is an unnecessary failure mode. `[IReactiveObject]` can bolt `IReactiveObject` onto a type that cannot inherit `ReactiveObject`, but `ViewModelBase` should stay `ObservableObject`.

Coexistence rule:

- Default: CommunityToolkit on `ViewModelBase`.
- New island: a `ReactiveObject` ViewModel for the Skia timeline only, hosted by the existing playback VM as a child (same composition style as `EmbeddedPlaybackPipelineViewModel`).
- Do not convert `SessionWorkflowCoordinator` from `ObservableObject` to `ReactiveObject`.

Fody (`ReactiveUI.Fody`) is obsolete relative to the source generators. Do not add Fody.

Package identity for Avalonia 12: use **`ReactiveUI.Avalonia`** (ReactiveUI org). The older Avalonia-team package `Avalonia.ReactiveUI` stopped at **11.3.9** and has no Avalonia 12 release.

A source-generator spike can reference `ReactiveUI` + `ReactiveUI.SourceGenerators` **without** `ReactiveUI.Avalonia`. Compiled AXAML bindings only need INPC. `WhenActivated` / `ReactiveUserControl<T>` need `ReactiveUI.Avalonia` and `AppBuilder.UseReactiveUI()`, which registers Splat. Keep that off the `--dub` composition root (`DubCli` uses the same coordinator construction as the desktop app).

| `ReactiveUI.Avalonia` | Avalonia floor (NuGet) | Use with Babel Player 12.0.1 |
|---|---|---|
| 12.0.3 (2026-06-04) | Avalonia 12.0 line | **Pin this** if the Avalonia integration package is added |
| 12.1.2 | Avalonia >= 12.1.2 | **Do not float here** without an Avalonia bump |

---

## 6. Integration blueprints (Settings consent and commands)

### 6.1 ChatterboxVoiceCloneConsent without new frameworks

Live control (already shipping):

```561:564:Views/MainWindow.axaml
                                <CheckBox Content="{local:Localize Check_ChatterboxCloneConsent}"
                                          IsChecked="{Binding Playback.ChatterboxVoiceCloneConsent, Mode=TwoWay}"
                                          IsVisible="{Binding Playback.IsChatterboxTtsSelected}"
                                          FontSize="12" />
```

Playback VM writes through to `AppSettings` immediately (`EmbeddedPlaybackViewModel.Settings.cs`). Settings window is a draft/apply dialog. If Settings should also show the flag, keep CommunityToolkit:

```csharp
// SettingsViewModel: snapshot in ctor from _coordinator.CurrentSettings
[ObservableProperty] private bool _chatterboxVoiceCloneConsent;

// Apply():
settings.ChatterboxVoiceCloneConsent = ChatterboxVoiceCloneConsent;
```

```xml
<!-- SettingsWindow.axaml, Models or General card -->
<CheckBox Content="{local:Localize Check_ChatterboxCloneConsent}"
          IsChecked="{Binding ChatterboxVoiceCloneConsent, Mode=TwoWay}"/>
```

Add a seam test next to `ChatterboxConsentTests` that the Settings AXAML contains the binding. Do not exercise the setter through a full coordinator on a non-pumping UI thread (the existing test comments document a deadlock risk).

A ReactiveUI version of the same toggle (`[Reactive] private bool _chatterboxVoiceCloneConsent`) is a lateral move. Do not introduce ReactiveUI.SourceGenerators for this property.

### 6.2 Command wrapping an inference pipeline task

Preferred: keep `EmbeddedPlaybackPipelineViewModel.RunPipelineAsync`. Status strings ("Transcribing...") and progress floats already come from `PipelineStageUpdate.Title` / `Detail` / `Progress01`. Bind those. Do not invent a parallel Zafiro command surface.

If a ReactiveCommand island is added later, wrap the coordinator; do not put `ReactiveCommand` inside it:

```csharp
RunPipelineCommand = ReactiveCommand.CreateFromTask(
    ct => coordinator.AdvancePipelineAsync(progress: null, stageProgress: uiProgress, cancellationToken: ct),
    this.WhenAnyValue(x => x.CanRunPipeline));
```

Cancellation stays a `CancellationTokenSource` owned by the ViewModel (`_pipelineCts` today). Dispose it on VM dispose.

---

## 7. Implementation Roadmap and Package Manifest

### 7.1 Required NuGet dependencies (proposed, not applied)

Do not add these for Milestone A.

```xml
<!-- Proposed only if Milestone B starts a ReactiveObject timeline VM.
     Pin 12.0.3 while Avalonia stays 12.0.1. Do not use Version="*" . -->
<ItemGroup>
  <PackageReference Include="ReactiveUI.Avalonia" Version="12.0.3" />
  <PackageReference Include="ReactiveUI.SourceGenerators" Version="3.2.0" PrivateAssets="all" />
  <!-- System.Reactive 6.1.0 is already referenced -->
</ItemGroup>
```

`Zafiro.Avalonia` 53.3.1 is **not** in the proposed manifest. Revisit only after all of:

1. An explicit Avalonia bump decision (12.0.4 minimum; 12.1.2 if `ReactiveUI.Avalonia` 12.1.x is desired). Also re-check `AvaloniaUI.DiagnosticsSupport` 2.2.1 against that bump.
2. A restore graph audit that pins `ReactiveUI.Avalonia` (11.4.13 if staying with Zafiro's pin, or 12.0.3 if staying on Avalonia 12.0.x) so 12.1.2 cannot sneak in.
3. A written answer for `ReactiveProperty` + `Serilog` (exclude, alias, or reject Zafiro).
4. A presentation-only project split, or compiled architecture tests that fail if `Zafiro` / `ReactiveUI` appear under `Services/`.
5. A product need Zafiro uniquely satisfies (SlimWizard legal consent, or a control we would otherwise write). A Settings checkbox is not that need.

Optional later: `ReactiveUI.Testing` for timeline VM tests that advance virtual clocks. Keep those tests small; if they need wall-clock waits, they belong in `Quarantined/`.

### 7.2 Phase-in strategy

1. **Milestone A (Settings consent):** CommunityToolkit property + Settings AXAML + `Apply()` persist. Keep the MainWindow live checkbox. No new packages. `scripts/check-architecture.py` already forbids ViewModels calling raw pipeline methods; add a grep that `Services/` does not gain `using ReactiveUI`, `using Zafiro`, or `using CSharpFunctionalExtensions`.
2. **Coordinator seam freeze:** Leave `Subject<ReadinessSignal>` as the only Rx type in Services. New signals should be `IObservable<T>` or `event` / `IProgress<T>`, never `ReactiveCommand`.
3. **Optional Reactive spike (not Milestone A):** `ReactiveUI` + `ReactiveUI.SourceGenerators` 3.2.0 on one new isolated VM. No `ReactiveUI.Avalonia`, no Splat, no `UseReactiveUI()`. Bindings stay compiled AXAML.
4. **Milestone B (timeline canvas):** Add Skia rendering in `Views/` (or a dedicated control). Introduce `System.Reactive` sampling on the UI scheduler first, still on a CommunityToolkit VM if possible. Add `ReactiveUI.Avalonia` 12.0.3 only if `WhenActivated` + `Switch()` seek pipelines prove cleaner than manual `IDisposable` fields.
5. **Zafiro:** Separate decision after an Avalonia pin bump and a restore-graph audit. Not part of v1.4 cloning. Not a dependency of the consent checkbox.

### 7.3 Open questions / blockers

- Will Avalonia stay on 12.0.1 through the timeline milestone? If yes, Zafiro 53.3.1 is blocked on the 12.0.4 floor.
- `ReactiveUI.Avalonia` 11.4.13 under Avalonia 12.0.4 is what Zafiro ships; binary compatibility is UNVERIFIED for this app until a smoke run.
- Analyzer skew: Dialogs 53.3.1 pins SourceGenerators 2.6.1 vs a direct 3.2.0 reference.
- `UseReactiveUI()` introduces Splat. That locator must never become required for `DubCli`.
- Should headless eventually be a second csproj so presentation packages cannot leak? Tenet 8 says yes eventually; this study does not start that split.
- Is a Settings duplicate of the MainWindow consent checkbox actually desired? The live control already satisfies "GUI toggle missing" from the handoff.
- Single-maintainer risk if Zafiro becomes load-bearing. MIT allows a fork; name that contingency before it is load-bearing.

---

## 8. How this document should be used

- **Do** use it to reject Zafiro for Settings and to pin ReactiveUI versions if that stack is added later.
- **Do** use the scheduler split when prototyping the Skia timeline.
- **Do not** update `docs/architecture.md` as if ReactiveUI or Zafiro are in the product.
- **Do not** add packages from section 7.1 without a milestone that needs them.

Sources: `BabelPlayer.csproj` (Avalonia 12.0.1, CommunityToolkit.Mvvm 8.4.2, System.Reactive 6.1.0); NuGet `Zafiro.Avalonia` 53.3.1, `ReactiveUI.Avalonia` 12.0.3 / 12.1.2, `ReactiveUI.SourceGenerators` 3.2.0; [Zafiro.Avalonia README](https://github.com/SuperJMN/Zafiro.Avalonia); [IEnhancedCommand.cs](https://github.com/SuperJMN/Zafiro/blob/master/src/Zafiro.UI/Commands/IEnhancedCommand.cs); [ReactiveUI Avalonia install docs](https://www.reactiveui.net/documentation/getting-started/installation/avalonia/).
