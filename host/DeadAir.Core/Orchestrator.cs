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
    private long _utteranceId;

    /// <summary>Per-utterance ASR timeout (spec §3/§4.1). Settable for tests; production
    /// default is 60s.</summary>
    internal int UtteranceTimeoutMs { get; set; } = 60_000;

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
        long myUtterance;
        lock (_gate)
        {
            if (State != FlowState.Recording) return;
            SetState(FlowState.Transcribing);
            _clock.Restart();
            myUtterance = ++_utteranceId;
        }
        ScheduleUtteranceTimeout(myUtterance);
        await sidecar.StopUtteranceAsync();
    }

    private void ScheduleUtteranceTimeout(long utteranceId)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(UtteranceTimeoutMs);
            lock (_gate)
            {
                if (_utteranceId != utteranceId || State != FlowState.Transcribing) return;
                SetState(FlowState.Idle);
            }
            notifier.Toast("ASR timed out");
        });
    }

    public async Task OnSidecarEventAsync(SidecarEvent e)
    {
        switch (e.Event)
        {
            case "ready":
                lock (_gate) { _utteranceId++; SetState(FlowState.Idle); }
                break;
            case "final":
                bool proceed;
                lock (_gate)
                {
                    proceed = State == FlowState.Transcribing;
                    if (proceed) { _utteranceId++; SetState(FlowState.Cleaning); }
                }
                if (proceed) await HandleFinalAsync(e);
                break;
            case "empty":
                lock (_gate) { _utteranceId++; SetState(FlowState.Idle); }
                break;
            case "degraded":
                if (!_degradedToastShown)
                {
                    _degradedToastShown = true;
                    notifier.Toast($"GPU unavailable ({e.Reason}) — using CPU");
                }
                break;
            case "error":
                lock (_gate) { _utteranceId++; SetState(FlowState.Idle); }
                notifier.Toast($"Error ({e.Where}): {e.Message}");
                break;
        }
    }

    private async Task HandleFinalAsync(SidecarEvent e)
    {
        try
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
        }
        finally
        {
            SetState(FlowState.Idle);
        }
    }
}
