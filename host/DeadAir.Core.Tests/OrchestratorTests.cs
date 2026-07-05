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
}
