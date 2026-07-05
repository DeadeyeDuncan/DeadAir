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
    private Process? _proc;
    private int _consecutiveFailures;
    private bool _shuttingDown;

    public event Action<SidecarEvent>? EventReceived;
    public event Action? Faulted;

    public SidecarManager(AppConfig config) => _config = config;

    public async Task LaunchAsync()
    {
        var s = _config.Sidecar;
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = s.Python,
            Arguments = QuoteArgsWithSpaces(s.Args),
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

        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(() => _proc.StandardError.ReadToEndAsync()); // drain
        await SendConfigAsync(_config);
    }

    // ProcessStartInfo.Arguments is a raw command-line string: a space
    // inside an unquoted path splits it into two argv entries. The real
    // config passes multi-flag strings ("-m asr_sidecar") that are meant
    // to be separate tokens, but callers (tests, or installs under
    // space-containing directories) may instead pass a single path that
    // itself contains spaces. If the whole string resolves to one file on
    // disk, treat it as a single argument and quote it; otherwise pass it
    // through untouched.
    private static string QuoteArgsWithSpaces(string args)
    {
        if (args.Length > 0 && args.Contains(' ') &&
            !(args.StartsWith('"') && args.EndsWith('"')) &&
            File.Exists(args))
        {
            return $"\"{args}\"";
        }
        return args;
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
