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
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = s.Python,
            Arguments = s.Args,
            WorkingDirectory = Path.GetFullPath(s.WorkingDir,
                AppContext.BaseDirectory),
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

    public Task SendConfigAsync(AppConfig c) =>
        SendAsync(ConfigCommand.From(c));

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
