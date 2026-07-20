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
    private TextWriter _log = null!;
    private System.Windows.Controls.MenuItem _modeMenuItem = null!;
    private System.Windows.Controls.MenuItem _translateMenuItem = null!;
    private RecordingIndicatorWindow _indicator = null!;
    private Mutex _singleInstance = null!;
    private FlowState _lastFlowState = FlowState.Idle;

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
        var logPath = Path.Combine(logDir, $"deadair-{DateTime.Now:yyyyMMdd}.log");
        var logStream = new FileStream(logPath,
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        // Synchronized: the warm-up task, Faulted stderr dump, LatencyLogged and
        // FireAndForget all write from pool threads — StreamWriter itself is not
        // thread-safe (interleaved writes corrupt lines).
        _log = TextWriter.Synchronized(new StreamWriter(logStream) { AutoFlush = true });
        _log.WriteLine($"{DateTime.Now:HH:mm:ss} app started, log={logPath}");

        // Skull-waveform identity on the tray; degrade to the system icon if the
        // loose asset is missing — an icon must never cost us the launch.
        var trayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "deadair.ico");
        _tray = new TaskbarIcon
        {
            ToolTipText = "DeadAir — starting…",
            Icon = File.Exists(trayIconPath)
                ? new System.Drawing.Icon(trayIconPath)
                : System.Drawing.SystemIcons.Application,
            ContextMenu = BuildMenu(),
        };
        _tray.ForceCreate();
        _indicator = new RecordingIndicatorWindow();
        _indicator.ApplyPillTuning(_config.Pill);
        var notifier = new TrayNotifier(_tray, Dispatcher, state =>
        {
            _lastFlowState = state;   // set on the dispatcher thread; read by the handlers below
            if (state == FlowState.Recording) { _indicator.ShowIndicator(); return; }
            var caption = PillStatus.ForState(state);
            if (caption is { } c) _indicator.ShowStatus(c.Text, c.Dismiss);
            // Idle and Cleaning map to null and are deliberately ignored: the
            // terminal outcome caption owns dismissal (hiding here would stomp
            // "sent" instantly), and Cleaning is captioned from CleaningStarted.
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
        _orchestrator.CleaningStarted += (mode, translating) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (PillStatus.SuppressTerminal(_lastFlowState)) return;
                var c = PillStatus.ForCleaning(mode, translating);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });

        _orchestrator.Outcome += o => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // A superseded utterance's tail still reports its outcome. If a
                // NEW recording is already live, drawing it would overwrite that
                // recording's scope and retract its pill -- so drop it.
                if (PillStatus.SuppressTerminal(_lastFlowState)) return;
                var c = PillStatus.ForOutcome(o);
                _indicator.ShowStatus(c.Text, c.Dismiss);
            }
            catch { /* indicator failures never break the pipeline */ }
        });
        _sidecar.EventReceived += ev =>
        {
            if (ev.Event == "waveform")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.PushWaveform(ev.Samples ?? Array.Empty<double>()); }
                    catch { }
                });
                return;
            }
            if (ev.Event == "partial")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { _indicator.SetPartial(ev.Text ?? ""); } catch { }
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

        // A typo'd hotkey name in config.json must degrade to the default,
        // not crash the launch (OnStartup is async void — a throw here kills
        // the app before the tray exists).
        int hotkeyVk;
        try { hotkeyVk = VkMap.Resolve(_config.Hotkey.Key); }
        catch (ArgumentException ex)
        {
            notifier.Toast($"{ex.Message} — using default hotkey RControl");
            hotkeyVk = VkMap.Resolve("RControl");
        }
        var machine = new HoldKeyStateMachine(hotkeyVk);
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
        menu.Style = (System.Windows.Style)Current.FindResource("DeadAirContextMenu");
        var itemStyle = (System.Windows.Style)Current.FindResource("DeadAirMenuItem");

        // IsChecked must reflect the loaded config (the orchestrator already
        // starts in config.Cleanup.Mode) and must be set BEFORE the handlers
        // attach — _orchestrator doesn't exist yet at menu-build time.
        var mode = new System.Windows.Controls.MenuItem
        {
            Header = "Polished mode", IsCheckable = true,
            IsChecked = _config.Cleanup.Mode == CleanupMode.Polished,
            Style = itemStyle,
        };
        mode.Checked += (_, _) => _orchestrator.Mode = CleanupMode.Polished;
        mode.Unchecked += (_, _) => _orchestrator.Mode = CleanupMode.Faithful;
        _modeMenuItem = mode;

        // Child clicks update the live config for the next utterance. Persistence
        // remains Settings-save-only, matching Polished mode's transient behavior.
        // Parent takes the submenu-capable style; DeadAirMenuItem is leaf-only.
        var translate = new System.Windows.Controls.MenuItem
        {
            Style = (System.Windows.Style)Current.FindResource("DeadAirSubmenuItem"),
        };
        _translateMenuItem = translate;
        SyncTranslationMenu();

        var settings = new System.Windows.Controls.MenuItem
        { Header = "Settings…", Style = itemStyle };
        settings.Click += (_, _) =>
            new SettingsWindow(_config, OnSettingsSaved).Show();

        var exit = new System.Windows.Controls.MenuItem { Header = "Exit", Style = itemStyle };
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
        menu.Items.Add(translate);
        menu.Items.Add(settings);
        menu.Items.Add(new System.Windows.Controls.Separator
        {
            Style = (System.Windows.Style)Current.FindResource("DeadAirSeparator"),
        });
        menu.Items.Add(exit);
        return menu;
    }

    private void SelectTranslationLanguage(string language)
    {
        _config.Cleanup.OutputLanguage = language;
        SyncTranslationMenu();
    }

    private void SyncTranslationMenu()
    {
        var state = TranslationMenuBuilder.Build(
            _config.Cleanup.OutputLanguage);
        var itemStyle = (System.Windows.Style)Current.FindResource(
            "DeadAirMenuItem");

        _translateMenuItem.Header = state.Header;
        _translateMenuItem.Items.Clear();
        foreach (var option in state.Options)
        {
            var child = new System.Windows.Controls.MenuItem
            {
                Header = option.Header,
                IsCheckable = true,
                IsChecked = option.IsChecked,
                Style = itemStyle,
            };
            var language = option.OutputLanguage;
            child.Click += (_, _) => SelectTranslationLanguage(language);
            _translateMenuItem.Items.Add(child);
        }
    }

    private async void OnSettingsSaved()
    {
        try
        {
            ConfigStore.Save(_config);
            _orchestrator.Mode = _config.Cleanup.Mode; // apply live, no restart needed
            _modeMenuItem.IsChecked = _config.Cleanup.Mode == CleanupMode.Polished;
            SyncTranslationMenu();
            _indicator.ApplyPillTuning(_config.Pill);
            // Host-only changes (cleanup mode, output language, Ollama, pill,
            // prompts) must not bounce the ASR engine — the manager skips the
            // send unless an ASR-relevant field changed.
            await _sidecar.SendConfigIfChangedAsync(_config); // hot-reload sidecar side
        }
        catch (Exception ex)
        {
            _log.WriteLine($"{DateTime.Now:HH:mm:ss} ERROR settings-saved: {ex}");
            _tray.ShowNotification("DeadAir", $"Couldn't apply settings: {ex.Message}");
        }
    }
}
