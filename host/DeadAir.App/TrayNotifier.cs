using System.Windows.Threading;
using H.NotifyIcon;
using DeadAir.Core;

namespace DeadAir.App;

public sealed class TrayNotifier(TaskbarIcon tray, Dispatcher dispatcher,
    Action<FlowState>? stateHook = null) : IUserNotifier
{
    public void SetState(FlowState state) => dispatcher.BeginInvoke(() =>
    {
        tray.ToolTipText = $"DeadAir — {state}";
        try { stateHook?.Invoke(state); }
        catch { /* indicator failures never break the pipeline */ }
    });

    public void Toast(string message) => dispatcher.BeginInvoke(() =>
        tray.ShowNotification("DeadAir", message));
}
