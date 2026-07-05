using System.Diagnostics;
using DeadAir.Core.Cleanup;
using DeadAir.Core.Config;
using DeadAir.Core.Inject;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core;

public enum FlowState { Idle, Recording, Transcribing, Cleaning, Injecting }

public interface IUserNotifier
{
    void SetState(FlowState state);
    void Toast(string message);
}

public sealed class Orchestrator(
    ISidecarControl sidecar, ITranscriptCleaner cleaner,
    ITextInjector injector, IUserNotifier notifier, AppConfig config)
{
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private bool _degradedToastShown;

    public FlowState State { get; private set; } = FlowState.Idle;
    public CleanupMode Mode { get; set; } = config.Cleanup.Mode;

    public event Action<string>? LatencyLogged;

    private void SetState(FlowState s) { State = s; notifier.SetState(s); }

    public async Task OnHotkeyDownAsync()
    {
        lock (_gate)
        {
            if (State != FlowState.Idle) return;
            SetState(FlowState.Recording);
        }
        await sidecar.StartUtteranceAsync();
    }

    public async Task OnHotkeyUpAsync()
    {
        lock (_gate)
        {
            if (State != FlowState.Recording) return;
            SetState(FlowState.Transcribing);
            _clock.Restart();
        }
        await sidecar.StopUtteranceAsync();
    }

    public async Task OnSidecarEventAsync(SidecarEvent e)
    {
        switch (e.Event)
        {
            case "final":
                bool proceed;
                lock (_gate)
                {
                    proceed = State == FlowState.Transcribing;
                    if (proceed) SetState(FlowState.Cleaning);
                }
                if (proceed) await HandleFinalAsync(e);
                break;
            case "empty":
                SetState(FlowState.Idle);
                break;
            case "degraded":
                if (!_degradedToastShown)
                {
                    _degradedToastShown = true;
                    notifier.Toast($"GPU unavailable ({e.Reason}) — using CPU");
                }
                break;
            case "error":
                notifier.Toast($"Error ({e.Where}): {e.Message}");
                SetState(FlowState.Idle);
                break;
        }
    }

    private async Task HandleFinalAsync(SidecarEvent e)
    {
        var asrMs = _clock.ElapsedMilliseconds;
        var result = await cleaner.CleanAsync(e.Text ?? "", Mode);
        var cleanMs = _clock.ElapsedMilliseconds - asrMs;
        if (result.Skipped && result.Reason != "below skip guard")
            notifier.Toast($"cleanup skipped: {result.Reason}");

        SetState(FlowState.Injecting);
        var ok = await injector.InjectAsync(result.Text);
        if (!ok)
            notifier.Toast("Couldn't insert — text on clipboard, press Ctrl+V");
        LatencyLogged?.Invoke(
            $"asr={e.Ms ?? asrMs}ms clean={cleanMs}ms " +
            $"total={_clock.ElapsedMilliseconds}ms chars={result.Text.Length}");
        SetState(FlowState.Idle);
    }
}
