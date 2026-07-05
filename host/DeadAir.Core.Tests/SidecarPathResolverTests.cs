using DeadAir.Core.Sidecar;

namespace DeadAir.Core.Tests;

public class SidecarPathResolverTests
{
    [Fact]
    public void BareCommand_PassesThroughUntouched()
    {
        var (python, work) = SidecarPathResolver.Resolve(
            AppContext.BaseDirectory, "python", AppContext.BaseDirectory);
        Assert.Equal("python", python);
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(work));
    }

    [Fact]
    public void ValidConfiguredPaths_UsedDirectly()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sc", "asr_sidecar"));
            var py = Path.Combine(root, "sc", "py.exe");
            File.WriteAllText(py, "");
            var (python, work) = SidecarPathResolver.Resolve(root, py, Path.Combine(root, "sc"));
            Assert.Equal(py, python);
            Assert.Equal(Path.Combine(root, "sc"), work);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BrokenConfig_DiscoversByWalkingUp()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var deep = Path.Combine(root, "host", "App", "bin", "Debug", "net8.0");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(root, "sidecar", "asr_sidecar"));
            var venvPy = Path.Combine(root, "sidecar", ".venv", "Scripts");
            Directory.CreateDirectory(venvPy);
            var py = Path.Combine(venvPy, "python.exe");
            File.WriteAllText(py, "");
            var (python, work) = SidecarPathResolver.Resolve(
                deep, @"..\..\sidecar\.venv\Scripts\python.exe", @"..\..\sidecar");
            Assert.Equal(py, python);
            Assert.Equal(Path.Combine(root, "sidecar"), work);
        }
        finally { Directory.Delete(root, true); }
    }
}
