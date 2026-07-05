using System.Windows.Threading;
using H.NotifyIcon;
using DeadAir.Core;

namespace DeadAir.App;

public sealed class TrayNotifier(TaskbarIcon tray, Dispatcher dispatcher,
    Action<FlowState>? stateHook = null) : IUserNotifier
{
    /// <summary>Active ASR engine ("gpu"/"cpu"), shown alongside state in the tray
    /// tooltip once known (spec C1: makes silent GPU-vs-CPU fallback visible).</summary>
    public string? EngineLabel { get; set; }

    public void SetState(FlowState state) => dispatcher.BeginInvoke(() =>
    {
        tray.ToolTipText = EngineLabel is null
            ? $"DeadAir — {state}"
            : $"DeadAir — {state} [{EngineLabel}]";
        try { stateHook?.Invoke(state); }
        catch { /* indicator failures never break the pipeline */ }
    });

    public void Toast(string message) => dispatcher.BeginInvoke(() =>
        tray.ShowNotification("DeadAir", message));
}
