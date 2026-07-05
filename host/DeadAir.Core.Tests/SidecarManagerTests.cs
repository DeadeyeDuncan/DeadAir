using DeadAir.Core.Config;
using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarManagerTests
{
    private static AppConfig FakeConfig() => new()
    {
        Sidecar = new SidecarLaunchConfig
        {
            Python = "python",
            Args = $"\"{Path.Combine(AppContext.BaseDirectory, "fixtures", "fake_sidecar.py")}\"",
            WorkingDir = AppContext.BaseDirectory,
        }
    };

    [Fact]
    public async Task Launch_ReceivesReady_ThenStartStopRoundTrips()
    {
        using var mgr = new SidecarManager(FakeConfig());
        var events = new List<SidecarEvent>();
        var final = new TaskCompletionSource<SidecarEvent>();
        mgr.EventReceived += e =>
        {
            events.Add(e);
            if (e.Event == "final") final.TrySetResult(e);
        };

        await mgr.LaunchAsync();
        await mgr.StartUtteranceAsync();
        await mgr.StopUtteranceAsync();

        var f = await final.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("hello world", f.Text);
        Assert.Contains(events, e => e.Event == "ready");
        await mgr.ShutdownAsync();
    }
}
