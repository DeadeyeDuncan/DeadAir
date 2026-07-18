using System.Diagnostics;
using System.Text.Json;
using DeadAir.Core.Config;

namespace DeadAir.Core.Sidecar;

public interface ISidecarControl
{
    Task StartUtteranceAsync();
    Task StopUtteranceAsync();
    Task CancelAsync();
}

public sealed class SidecarManager : ISidecarControl, IDisposable
{
    private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };
    private readonly AppConfig _config;
    private readonly Queue<string> _stderrLines = new();
    private Process? _proc;
    private int _consecutiveFailures;
    private volatile bool _shuttingDown;

    public event Action<SidecarEvent>? EventReceived;
    public event Action? Faulted;

    public string RecentStderr { get { lock (_stderrLines) return string.Join(Environment.NewLine, _stderrLines); } }

    public SidecarManager(AppConfig config) => _config = config;

    public async Task LaunchAsync()
    {
        var s = _config.Sidecar;
        var (python, workingDir) = SidecarPathResolver.Resolve(
            AppContext.BaseDirectory, s.Python, s.WorkingDir);
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = python,
            Arguments = s.Args,
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        }) ?? throw new InvalidOperationException("failed to start sidecar");
        _proc.StandardInput.AutoFlush = true;

        _ = Task.Run(async () =>
        {
            try { await ReadLoopAsync(); }
            catch { Faulted?.Invoke(); }
        });

        var proc = _proc!;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_stderrLines)
                    {
                        _stderrLines.Enqueue(line);
                        while (_stderrLines.Count > 50) _stderrLines.Dequeue();
                    }
                }
            }
            catch { /* stream closed */ }
        });
        await SendConfigAsync(_config);
    }

    private async Task ReadLoopAsync()
    {
        var proc = _proc!;
        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<SidecarEvent>(line);
                if (e is not null)
                {
                    _consecutiveFailures = 0;
                    EventReceived?.Invoke(e);
                }
            }
            catch (JsonException) { /* garbage line — ignore */ }
        }
        if (!_shuttingDown) await RestartAsync();
    }

    private async Task RestartAsync()
    {
        if (_consecutiveFailures >= 5) { Faulted?.Invoke(); return; }
        var delay = BackoffSeconds[Math.Min(_consecutiveFailures,
            BackoffSeconds.Length - 1)];
        _consecutiveFailures++;
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (!_shuttingDown) await LaunchAsync();
    }

    private string _lastSentConfigJson = "";

    public async Task SendConfigAsync(AppConfig c)
    {
        var cmd = ConfigCommand.From(c);
        await SendAsync(cmd);
        // Only a config the sidecar actually received updates the baseline —
        // a failed send must leave it stale so the next save re-sends.
        _lastSentConfigJson = JsonSerializer.Serialize(cmd);
    }

    /// <summary>Send config only if it differs from the last successfully
    /// sent payload. Keeps host-only settings saves (cleanup/Ollama/pill)
    /// from bouncing the sidecar's ASR engine. The baseline lives here, not
    /// in the App, because LaunchAsync/RestartAsync also send config — every
    /// successful send through this class refreshes the same baseline, so a
    /// crash-restart during a failed save can never strand it.</summary>
    public async Task<bool> SendConfigIfChangedAsync(AppConfig c)
    {
        if (JsonSerializer.Serialize(ConfigCommand.From(c)) == _lastSentConfigJson)
            return false;
        await SendConfigAsync(c);
        return true;
    }

    public Task StartUtteranceAsync() => SendAsync(new SimpleCommand("start"));
    public Task StopUtteranceAsync() => SendAsync(new SimpleCommand("stop"));
    public Task CancelAsync() => SendAsync(new SimpleCommand("cancel"));

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        try { await SendAsync(new SimpleCommand("shutdown")); } catch { }
        if (_proc is not null && !_proc.WaitForExit(5000)) _proc.Kill();
    }

    private async Task SendAsync(object cmd)
    {
        if (_proc is null) throw new InvalidOperationException("not launched");
        await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(cmd));
    }

    public void Dispose()
    {
        _shuttingDown = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
    }
}
