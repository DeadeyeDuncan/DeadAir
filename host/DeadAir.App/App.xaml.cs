using System.IO;
using System.Threading;
using System.Windows;
using H.NotifyIcon;
using DeadAir.Core;
using DeadAir.Core.Cleanup;
using DeadAir.Core.Config;
using DeadAir.Core.Hotkey;
using DeadAir.Core.Inject;
using DeadAir.Core.Sidecar;

namespace DeadAir.App;

public partial class App : Application
{
    private AppConfig _config = null!;
    private SidecarManager _sidecar = null!;
    private Orchestrator _orchestrator = null!;
    private KeyboardHook _hook = null!;
    private TaskbarIcon _tray = null!;
    private StreamWriter _log = null!;
    private RecordingIndicatorWindow _indicator = null!;
    private Mutex _singleInstance = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard. DeadAir lives in the tray, so a second launch
        // is almost always accidental — and would collide on the day's log file
        // and hard-crash (IOException). Detect the running instance and bow out.
        _singleInstance = new Mutex(initiallyOwned: true,
            @"Local\DeadAir.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("DeadAir is already running — see the system tray.",
                "DeadAir", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _config = ConfigStore.Load();

        var logDir = Path.Combine(Path.GetDirectoryName(
            ConfigStore.DefaultPath)!, "logs");
        Directory.CreateDirectory(logDir);
        // FileShare.ReadWrite so a lingering/zombie handle can never turn a log
        // open into a launch-killing crash (defense-in-depth behind the mutex).
        var logStream = new FileStream(Path.Combine(logDir,
            $"deadair-{DateTime.Now:yyyyMMdd}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _log = new StreamWriter(logStream) { AutoFlush = true };
        _log.WriteLine($"{DateTime.Now:HH:mm:ss} app started, log={((FileStream)_log.BaseStream).Name}");

        _tray = new TaskbarIcon
        {
            ToolTipText = "DeadAir — starting…",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
        _tray.ForceCreate();
        _indicator = new RecordingIndicatorWindow();
        var notifier = new TrayNotifier(_tray, Dispatcher, state =>
        {
            if (state == FlowState.Recording) _indicator.ShowIndicator();
            else _indicator.HideIndicator();
        });

        var clipboard = new WpfClipboard(Dispatcher);
        var injector = new CompositeInjector(new IInjectionStrategy[]
        {
            new ClipboardPasteInjector(clipboard, NativeInput.SendCtrlV,
                _config.Inject.RestoreClipboardDelayMs),
            new SendInputInjector(),
        }, clipboard);

        _sidecar = new SidecarManager(_config);
        var cleaner = new OllamaClient(_config);
        _ = Task.Run(async () =>
        {
            var ok = await cleaner.WarmUpAsync();
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} ollama warm-up " +
                (ok ? "ok" : "failed (will load on first use)"));
        });
        _orchestrator = new Orchestrator(_sidecar, cleaner, injector,
            notifier, _config);
        _orchestrator.LatencyLogged += line =>
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        _sidecar.EventReceived += ev =>
        {
            if (ev.Event == "level")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.Push(ev.Rms ?? 0); } catch { }
                });
                return;
            }
            // Track the active engine for tray-tooltip visibility (spec C1) — record only,
            // still forward to the orchestrator below exactly as before.
            if (ev.Event is "ready" && ev.Engine is { } engine)
                Dispatcher.BeginInvoke(() => notifier.EngineLabel = engine);
            else if (ev.Event is "degraded")
                Dispatcher.BeginInvoke(() => notifier.EngineLabel = "cpu");
            FireAndForget(() => _orchestrator.OnSidecarEventAsync(ev),
                "sidecar-event");
        };
        _sidecar.Faulted += () =>
        {
            // Actually put the cause IN the log the toast tells the user to check.
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} SIDECAR FAULTED — recent stderr:");
            _log.WriteLine(_sidecar.RecentStderr);
            notifier.Toast("Sidecar keeps crashing — check logs.");
        };

        var machine = new HoldKeyStateMachine(
            VkMap.Resolve(_config.Hotkey.Key));
        // Hook-thread callbacks must return fast: fire-and-forget to the pool.
        machine.HoldStarted += () => FireAndForget(_orchestrator.OnHotkeyDownAsync, "hotkey-down");
        machine.HoldEnded += () => FireAndForget(_orchestrator.OnHotkeyUpAsync, "hotkey-up");
        _hook = new KeyboardHook(machine);

        try { await _sidecar.LaunchAsync(); }
        catch (Exception ex)
        { notifier.Toast($"Sidecar failed to start: {ex.Message}"); }
    }

    private void FireAndForget(Func<Task> action, string where)
    {
        _ = Task.Run(async () =>
        {
            try { await action(); }
            catch (Exception ex)
            {
                _log.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR {where}: {ex}");
                _ = Dispatcher.BeginInvoke(() =>
                    _tray.ShowNotification("DeadAir", $"Error ({where}): {ex.Message}"));
            }
        });
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var mode = new System.Windows.Controls.MenuItem
        { Header = "Polished mode", IsCheckable = true };
        mode.Checked += (_, _) => _orchestrator.Mode = CleanupMode.Polished;
        mode.Unchecked += (_, _) => _orchestrator.Mode = CleanupMode.Faithful;

        var settings = new System.Windows.Controls.MenuItem
        { Header = "Settings…" };
        settings.Click += (_, _) =>
            new SettingsWindow(_config, OnSettingsSaved).Show();

        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += async (_, _) =>
        {
            try
            {
                _hook.Dispose();
                await _sidecar.ShutdownAsync();
            }
            catch (Exception ex) { _log.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR exit: {ex}"); }
            finally
            {
                try { _indicator.Close(); } catch { }
                _log.Dispose();
                try { _singleInstance.ReleaseMutex(); } catch { }
                _singleInstance.Dispose();
                Shutdown();
            }
        };

        menu.Items.Add(mode);
        menu.Items.Add(settings);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private async void OnSettingsSaved()
    {
        try
        {
            ConfigStore.Save(_config);
            _orchestrator.Mode = _config.Cleanup.Mode; // apply live, no restart needed
            await _sidecar.SendConfigAsync(_config); // hot-reload sidecar side
        }
        catch (Exception ex)
        {
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR settings-saved: {ex}");
            _tray.ShowNotification("DeadAir", $"Couldn't apply settings: {ex.Message}");
        }
    }
}
