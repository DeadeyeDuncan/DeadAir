using System.IO;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _config = ConfigStore.Load();

        var logDir = Path.Combine(Path.GetDirectoryName(
            ConfigStore.DefaultPath)!, "logs");
        Directory.CreateDirectory(logDir);
        _log = new StreamWriter(Path.Combine(logDir,
            $"deadair-{DateTime.Now:yyyyMMdd}.log"), append: true)
        { AutoFlush = true };

        _tray = new TaskbarIcon
        {
            ToolTipText = "DeadAir — starting…",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
        _tray.ForceCreate();
        var notifier = new TrayNotifier(_tray, Dispatcher);

        var clipboard = new WpfClipboard(Dispatcher);
        var injector = new CompositeInjector(new IInjectionStrategy[]
        {
            new ClipboardPasteInjector(clipboard, NativeInput.SendCtrlV,
                _config.Inject.RestoreClipboardDelayMs),
            new SendInputInjector(),
        }, clipboard);

        _sidecar = new SidecarManager(_config);
        var cleaner = new OllamaClient(_config);
        _orchestrator = new Orchestrator(_sidecar, cleaner, injector,
            notifier, _config);
        _orchestrator.LatencyLogged += line =>
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        _sidecar.EventReceived += ev =>
            _ = _orchestrator.OnSidecarEventAsync(ev);
        _sidecar.Faulted += () =>
            notifier.Toast("Sidecar keeps crashing — check logs.");

        var machine = new HoldKeyStateMachine(
            VkMap.Resolve(_config.Hotkey.Key));
        // Hook-thread callbacks must return fast: fire-and-forget to the pool.
        machine.HoldStarted += () => _ = _orchestrator.OnHotkeyDownAsync();
        machine.HoldEnded += () => _ = _orchestrator.OnHotkeyUpAsync();
        _hook = new KeyboardHook(machine);

        try { await _sidecar.LaunchAsync(); }
        catch (Exception ex)
        { notifier.Toast($"Sidecar failed to start: {ex.Message}"); }
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
            _hook.Dispose();
            await _sidecar.ShutdownAsync();
            Shutdown();
        };

        menu.Items.Add(mode);
        menu.Items.Add(settings);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private async void OnSettingsSaved()
    {
        ConfigStore.Save(_config);
        await _sidecar.SendConfigAsync(_config); // hot-reload sidecar side
    }
}
