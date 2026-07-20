using System.Linq;
using DeadAir.Core;
using DeadAir.Core.Cleanup;
using DeadAir.Core.Config;
using DeadAir.Core.Inject;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

sealed class FakeSidecar : ISidecarControl
{
    public int Starts, Stops, Cancels;
    public Task StartUtteranceAsync() { Starts++; return Task.CompletedTask; }
    public Task StopUtteranceAsync() { Stops++; return Task.CompletedTask; }
    public Task CancelAsync() { Cancels++; return Task.CompletedTask; }
}

sealed class FakeCleaner(CleanupResult result) : ITranscriptCleaner
{
    public string? SeenTranscript;
    public Task<CleanupResult> CleanAsync(string t, CleanupMode m,
        CancellationToken ct = default)
    { SeenTranscript = t; return Task.FromResult(result); }
}

sealed class FakeInjector(bool ok) : ITextInjector
{
    public string? Injected;
    public Task<bool> InjectAsync(string text)
    { Injected = text; return Task.FromResult(ok); }
}

sealed class ThrowingInjector : ITextInjector
{
    public Task<bool> InjectAsync(string text) =>
        throw new InvalidOperationException("injector exploded");
}

sealed class BlockingCleaner : ITranscriptCleaner
{
    public readonly TaskCompletionSource<CleanupResult> Gate = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<CleanupResult> CleanAsync(string t, CleanupMode m,
        CancellationToken ct = default) => Gate.Task;
}

sealed class FakeNotifier : IUserNotifier
{
    public List<string> Toasts = new();
    public FlowState LastState;
    public void SetState(FlowState s) => LastState = s;
    public void Toast(string m) => Toasts.Add(m);
}

public class OrchestratorTests
{
    private static Orchestrator Make(FakeSidecar sc, ITranscriptCleaner cl,
        ITextInjector inj, FakeNotifier n) =>
        new(sc, cl, inj, n, new AppConfig());

    [Fact]
    public async Task HappyPath_CleansAndInjects()
    {
        var sc = new FakeSidecar();
        var cl = new FakeCleaner(new CleanupResult("Clean text.", false, null));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = Make(sc, cl, inj, n);

        await o.OnHotkeyDownAsync();
        Assert.Equal(FlowState.Recording, o.State);
        Assert.Equal(1, sc.Starts);

        await o.OnHotkeyUpAsync();
        Assert.Equal(FlowState.Transcribing, o.State);
        Assert.Equal(1, sc.Stops);

        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw text", Ms = 500 });
        Assert.Equal("raw text", cl.SeenTranscript);
        Assert.Equal("Clean text.", inj.Injected);
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task CleanupFailure_InjectsRaw_AndToasts()
    {
        var cl = new FakeCleaner(new CleanupResult("raw words here that are long",
            true, "connection refused"));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), cl, inj, n);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw words here that are long" });
        Assert.Equal("raw words here that are long", inj.Injected);
        Assert.Contains(n.Toasts, t => t.Contains("cleanup skipped"));
    }

    [Fact]
    public async Task CleanupFailure_WhileTranslating_ToastsTranslationSkipped()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var cl = new FakeCleaner(new CleanupResult("raw words here that are long",
            true, "connection refused"));
        var inj = new FakeInjector(ok: true);
        var n = new FakeNotifier();
        var o = new Orchestrator(new FakeSidecar(), cl, inj, n, cfg);

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "raw words here that are long" });

        Assert.Equal("raw words here that are long", inj.Injected);
        Assert.Contains(n.Toasts, t =>
            t.Contains("translation skipped") && t.Contains("connection refused"));
        Assert.DoesNotContain(n.Toasts, t => t.Contains("cleanup skipped"));
        // The toast fires before injection, so it must not claim the English
        // text already landed.
        Assert.DoesNotContain(n.Toasts, t => t.Contains("injected"));
    }

    [Fact]
    public async Task InjectFailure_ToastsClipboardHint()
    {
        var cl = new FakeCleaner(new CleanupResult("text", true, "below skip guard"));
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(), cl, new FakeInjector(ok: false), n);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "final", Text = "text" });
        Assert.Contains(n.Toasts, t => t.Contains("Ctrl+V"));
    }

    [Fact]
    public async Task EmptyEvent_ReturnsToIdle_NoInjection()
    {
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)), inj,
            new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "empty" });
        Assert.Null(inj.Injected);
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task HotkeyDown_WhileBusy_Ignored()
    {
        var sc = new FakeSidecar();
        var o = Make(sc, new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyDownAsync(); // second press while recording
        Assert.Equal(1, sc.Starts);
    }

    [Fact]
    public async Task StaleFinal_WhileIdle_Ignored()
    {
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)), inj,
            new FakeNotifier());
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "ghost" });
        Assert.Null(inj.Injected);
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task Degraded_ToastsOnlyOnce()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "degraded", Reason = "no vulkan" });
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "degraded", Reason = "no vulkan" });
        Assert.Single(n.Toasts.Where(t => t.Contains("CPU")));
    }

    [Fact]
    public async Task ErrorEvent_Toasts_AndReturnsToIdle()
    {
        var n = new FakeNotifier();
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        await o.OnHotkeyDownAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "error", Where = "asr", Message = "boom" });
        Assert.Contains(n.Toasts, t => t.Contains("asr") && t.Contains("boom"));
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task ReadyEvent_ResetsWedgedState_AndNextHotkeyWorks()
    {
        var sc = new FakeSidecar();
        var o = Make(sc, new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());

        // Drive into Transcribing (a "wedged" mid-flow state after a sidecar restart).
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        Assert.Equal(FlowState.Transcribing, o.State);

        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Equal(FlowState.Idle, o.State);

        // Next hotkey-down must actually start a new utterance (proves not still wedged).
        await o.OnHotkeyDownAsync();
        Assert.Equal(FlowState.Recording, o.State);
        Assert.Equal(2, sc.Starts);
    }

    [Fact]
    public async Task HandleFinal_InjectorThrows_StillReturnsToIdle()
    {
        var n = new FakeNotifier();
        var cl = new FakeCleaner(new CleanupResult("clean text", false, null));
        var o = Make(new FakeSidecar(), cl, new ThrowingInjector(), n);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        // The throw propagates (callers like App's FireAndForget already log+toast it),
        // but the `finally` must still guarantee the state machine returns to Idle —
        // pre-fix, a throw here left the orchestrator wedged in Cleaning forever.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "raw" }));
        Assert.Equal(FlowState.Idle, o.State);
    }

    [Fact]
    public async Task ReadyEvent_MidCleaning_DoesNotForceIdle()
    {
        // An unsolicited ready (sidecar crash-restart resends config -> ready)
        // arriving while HandleFinalAsync owns the state must NOT force Idle —
        // that would let a new Recording start that the finally then stomps.
        var cl = new BlockingCleaner();
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(), cl, inj, new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "raw" });
        Assert.Equal(FlowState.Cleaning, o.State);

        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Equal(FlowState.Cleaning, o.State);   // not stomped

        cl.Gate.SetResult(new CleanupResult("clean", false, null));
        await finalTask;
        Assert.Equal(FlowState.Idle, o.State);       // final path still lands at Idle
        Assert.Equal("clean", inj.Injected);
    }

    [Fact]
    public async Task HandleFinal_TailDoesNotStompNewRecording()
    {
        // If something legitimately resets to Idle mid-cleanup (unsolicited
        // error) and the user starts a NEW utterance, the old utterance's
        // in-flight tail must still inject its words but must NOT demote the
        // new Recording via SetState(Injecting)/finally-Idle.
        var cl = new BlockingCleaner();
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(), cl, inj, new FakeNotifier());
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "raw" });
        Assert.Equal(FlowState.Cleaning, o.State);

        await o.OnSidecarEventAsync(
            new SidecarEvent { Event = "error", Where = "asr", Message = "x" });
        await o.OnHotkeyDownAsync();
        Assert.Equal(FlowState.Recording, o.State);

        cl.Gate.SetResult(new CleanupResult("clean", false, null));
        await finalTask;
        Assert.Equal(FlowState.Recording, o.State);  // new utterance untouched
        Assert.Equal("clean", inj.Injected);         // words never lost
    }

    [Fact]
    public async Task PartialAndWaveformEvents_NeverInjectOrChangeState()
    {
        // Invariant 1 pinned at the Core layer: interim events are no-ops to
        // the orchestrator in every state (routing away happens in the
        // untested WPF layer; this pin survives even if that routing changes).
        var inj = new FakeInjector(ok: true);
        var o = Make(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("x", false, null)), inj,
            new FakeNotifier());

        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "partial", Text = "ghost", Seq = 1 });
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "waveform", Samples = new[] { 0.1, -0.1 } });
        Assert.Equal(FlowState.Idle, o.State);
        Assert.Null(inj.Injected);

        await o.OnHotkeyDownAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        { Event = "partial", Text = "ghost two", Seq = 2 });
        Assert.Equal(FlowState.Recording, o.State);
        Assert.Null(inj.Injected);
    }

    [Fact]
    public async Task UtteranceTimeout_TranscribingTooLong_ToastsAndReturnsIdle()
    {
        var sc = new FakeSidecar();
        var n = new FakeNotifier();
        var o = Make(sc, new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), n);
        o.UtteranceTimeoutMs = 50;

        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        Assert.Equal(FlowState.Transcribing, o.State);

        await Task.Delay(150);

        Assert.Equal(FlowState.Idle, o.State);
        Assert.Contains(n.Toasts, t => t.Contains("timed out"));
    }

    [Fact]
    public async Task UtteranceTimeout_StaleTimerDoesNotFireOnLaterUtterance()
    {
        var sc = new FakeSidecar();
        var cl = new FakeCleaner(new CleanupResult("clean", false, null));
        var inj = new FakeInjector(true);
        var n = new FakeNotifier();
        var o = Make(sc, cl, inj, n);
        o.UtteranceTimeoutMs = 80;

        // First utterance completes normally — its 80ms timer is now stale.
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "one" });
        Assert.Equal(FlowState.Idle, o.State);

        // Second utterance uses a much longer timeout so only a buggy *stale* firing
        // from utterance 1's timer could produce a toast/Idle transition here.
        o.UtteranceTimeoutMs = 10_000;
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();

        await Task.Delay(150); // long enough for the stale first timer to have fired

        Assert.DoesNotContain(n.Toasts, t => t.Contains("timed out"));
        Assert.Equal(FlowState.Transcribing, o.State); // second utterance untouched
    }

    private static List<FlowOutcome> Watch(Orchestrator o)
    {
        var seen = new List<FlowOutcome>();
        o.Outcome += x => seen.Add(x);
        return seen;
    }

    [Fact]
    public async Task EmptyEvent_RaisesNothingHeard()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "empty" });
        Assert.Equal(new[] { FlowOutcome.NothingHeard }, seen);
    }

    [Fact]
    public async Task ErrorEvent_RaisesFailed()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent
        {
            Event = "error", Where = "asr", Message = "boom",
        });
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task SuccessfulInject_RaisesInjected()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        Assert.Equal(new[] { FlowOutcome.Injected }, seen);
    }

    [Fact]
    public async Task FailedInject_RaisesFailed()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(false), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task ThrowingInjector_StillRaisesFailedFromFinally()
    {
        // The existing suite pins that this exception PROPAGATES
        // (HandleFinal_InjectorThrows_StillReturnsToIdle uses ThrowsAsync).
        // The outcome must still be raised from the finally before it escapes.
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("hello", false, null)),
            new ThrowingInjector(), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" }));
        Assert.Equal(new[] { FlowOutcome.Failed }, seen);
    }

    [Fact]
    public async Task UtteranceTimeout_RaisesTimedOut()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        o.UtteranceTimeoutMs = 30;
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await Task.Delay(300);
        Assert.Equal(new[] { FlowOutcome.TimedOut }, seen);
    }

    [Fact]
    public async Task ReadyDuringTranscribing_RaisesInterrupted()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Equal(new[] { FlowOutcome.Interrupted }, seen);
    }

    [Fact]
    public async Task ReadyWhileIdle_RaisesNothing()
    {
        var o = Make(new FakeSidecar(), new FakeCleaner(new CleanupResult("x", false, null)),
            new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        Assert.Empty(seen);
    }

    [Fact]
    public async Task ReadyMidCleaning_StillLetsTheFinalTailRaiseInjected()
    {
        // ready mid-Cleaning must not force Idle (pinned by
        // ReadyEvent_MidCleaning_DoesNotForceIdle) and must not swallow the
        // tail's outcome: the words still land, so "sent" must still be raised.
        var cl = new BlockingCleaner();
        var o = Make(new FakeSidecar(), cl, new FakeInjector(true), new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "hello" });
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "ready", Engine = "gpu" });
        cl.Gate.SetResult(new CleanupResult("hello", false, null));
        await finalTask;
        Assert.Equal(new[] { FlowOutcome.Injected }, seen);
    }

    [Fact]
    public async Task SupersededTail_LastWins_FailedThenInjected()
    {
        // Spec: terminal captions are LAST-WINS, not exactly-once. This is the
        // pinned superseded-tail sequence WITHOUT a new recording: an
        // unsolicited error resets mid-Cleaning (raises Failed), then the old
        // tail still injects its words (raises Injected). Both raises are
        // correct; the App draws both briefly and the later, truthful "sent"
        // wins. Do NOT "fix" this to raise once — two ownership schemes died
        // trying (see the spec's design-history note).
        var cl = new BlockingCleaner();
        var inj = new FakeInjector(true);
        var o = Make(new FakeSidecar(), cl, inj, new FakeNotifier());
        var seen = Watch(o);
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        var finalTask = o.OnSidecarEventAsync(
            new SidecarEvent { Event = "final", Text = "old words" });
        await o.OnSidecarEventAsync(new SidecarEvent
        {
            Event = "error", Where = "asr", Message = "unsolicited",
        });
        cl.Gate.SetResult(new CleanupResult("old words", false, null));
        await finalTask;
        Assert.Equal("old words", inj.Injected);                          // words are never lost
        Assert.Equal(new[] { FlowOutcome.Failed, FlowOutcome.Injected }, seen);  // last-wins pair
    }

    [Fact]
    public async Task CleaningStarted_ReportsModeAndTranslating()
    {
        var cfg = new AppConfig();
        cfg.Cleanup.Mode = CleanupMode.Polished;
        var o = new Orchestrator(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("hello", false, null)),
            new FakeInjector(true), new FakeNotifier(), cfg);
        var seen = new List<(CleanupMode, bool)>();
        o.CleaningStarted += (m, t) => seen.Add((m, t));
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hello" });
        var (mode, translating) = Assert.Single(seen);
        Assert.Equal(CleanupMode.Polished, mode);
        Assert.Equal(cfg.Cleanup.TranslationActive, translating);
    }

    [Fact]
    public async Task CleaningStarted_ReportsTranslatingTrue_WhenOutputLanguageActive()
    {
        // Review finding: the sibling test's default config makes
        // TranslationActive false, so a hard-coded `false` in the raise would
        // pass the whole suite. This pins the true half — the feature's
        // headline "translating…" caption depends on it.
        var cfg = new AppConfig();
        cfg.Cleanup.OutputLanguage = "Spanish";
        var o = new Orchestrator(new FakeSidecar(),
            new FakeCleaner(new CleanupResult("hola", false, null)),
            new FakeInjector(true), new FakeNotifier(), cfg);
        var seen = new List<(CleanupMode, bool)>();
        o.CleaningStarted += (m, t) => seen.Add((m, t));
        await o.OnHotkeyDownAsync();
        await o.OnHotkeyUpAsync();
        await o.OnSidecarEventAsync(new SidecarEvent { Event = "final", Text = "hola" });
        var (_, translating) = Assert.Single(seen);
        Assert.True(translating);
    }
}
