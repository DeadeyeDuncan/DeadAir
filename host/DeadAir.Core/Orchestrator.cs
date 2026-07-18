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
                lock (_gate)
                {
                    _utteranceId++;
                    // Don't force Idle while HandleFinalAsync owns the state
                    // (Cleaning/Injecting): its finally lands at Idle anyway,
                    // and forcing Idle here would let a new Recording start
                    // that the finally then stomps (silent utterance loss).
                    if (State is not FlowState.Cleaning and not FlowState.Injecting)
                        SetState(FlowState.Idle);
                }
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
            // Snapshot before the await: the user can flip the tray toggle
            // while cleanup is in flight, and the toast must describe the
            // operation that was actually attempted.
            var translating = config.Cleanup.TranslationActive;
            var result = await cleaner.CleanAsync(e.Text ?? "", Mode);
            var cleanMs = _clock.ElapsedMilliseconds - asrMs;
            if (result.Skipped && result.Reason != "below skip guard")
                notifier.Toast(translating
                    ? $"translation skipped: {result.Reason}"
                    : $"cleanup skipped: {result.Reason}");

            // Only advance the state if this utterance still owns it — an
            // unsolicited reset (error/empty) may have moved on, possibly into
            // a NEW Recording. The injection itself still happens either way:
            // words are never lost.
            lock (_gate)
            {
                if (State == FlowState.Cleaning) SetState(FlowState.Injecting);
            }
            var ok = await injector.InjectAsync(result.Text);
            if (!ok)
                notifier.Toast("Couldn't insert — text on clipboard, press Ctrl+V");
            LatencyLogged?.Invoke(
                $"asr={e.Ms ?? asrMs}ms clean={cleanMs}ms " +
                $"total={_clock.ElapsedMilliseconds}ms chars={result.Text.Length}");
        }
        finally
        {
            // Land at Idle only from our own states — never demote a new
            // Recording that started after an unsolicited mid-cleanup reset.
            lock (_gate)
            {
                if (State is FlowState.Cleaning or FlowState.Injecting)
                    SetState(FlowState.Idle);
            }
        }
    }
}
