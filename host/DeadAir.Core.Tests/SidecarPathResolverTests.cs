using DeadAir.Core.Config;
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

public class SidecarPathResolver_ResolveAssetTests
{
    [Fact]
    public void ConfiguredPathExists_UsedDirectly()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var baseDir = Path.Combine(root, "app");
            Directory.CreateDirectory(baseDir);
            var asset = Path.Combine(baseDir, "x", "y.bin");
            Directory.CreateDirectory(Path.Combine(baseDir, "x"));
            File.WriteAllText(asset, "");

            var resolved = SidecarPathResolver.ResolveAsset(
                baseDir, Path.Combine("x", "y.bin"), Path.Combine("x", "y.bin"));

            Assert.Equal(Path.GetFullPath(asset), resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BrokenConfig_ButRootCopyExists_WalksUpToRoot()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // Deep baseDir where the relative-from-baseDir configured path is broken.
            var deep = Path.Combine(root, "host", "DeadAir.App", "bin", "Debug", "net8.0-windows");
            Directory.CreateDirectory(deep);

            // Real file lives at <root>\tools\whisper\whisper-server.exe
            var toolsDir = Path.Combine(root, "tools", "whisper");
            Directory.CreateDirectory(toolsDir);
            var realExe = Path.Combine(toolsDir, "whisper-server.exe");
            File.WriteAllText(realExe, "");

            var resolved = SidecarPathResolver.ResolveAsset(
                deep, @"..\..\tools\whisper\whisper-server.exe",
                Path.Combine("tools", "whisper", "whisper-server.exe"));

            Assert.Equal(Path.GetFullPath(realExe), resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NothingFound_ReturnsConfiguredResolvedValue()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var deep = Path.Combine(root, "host", "DeadAir.App", "bin", "Debug", "net8.0-windows");
            Directory.CreateDirectory(deep);

            var configured = @"..\..\tools\whisper\whisper-server.exe";
            var resolved = SidecarPathResolver.ResolveAsset(
                deep, configured, Path.Combine("tools", "whisper", "whisper-server.exe"));

            Assert.Equal(Path.GetFullPath(configured, deep), resolved);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class ConfigCommand_GpuAssetResolutionTests
{
    // Regression test for the real bug: the test assembly sits at the same depth under
    // the repo root as DeadAir.App's bin dir (host\X\bin\Debug\netX\), so resolving
    // against the actual AppContext.BaseDirectory here reproduces the production failure
    // (configured "..\..\..." was two segments short) and proves the walk-up fix finds
    // the real repo-root files.
    [Fact]
    public void DefaultConfig_ResolvesToRealRepoRootAssets()
    {
        var cfg = new AppConfig();
        var cmd = ConfigCommand.From(cfg);

        Assert.True(File.Exists(cmd.GpuServerExe),
            $"expected GpuServerExe to resolve to an existing file, got: {cmd.GpuServerExe}");
        Assert.True(File.Exists(cmd.GpuModelPath),
            $"expected GpuModelPath to resolve to an existing file, got: {cmd.GpuModelPath}");
        Assert.EndsWith("whisper-server.exe", cmd.GpuServerExe);
        Assert.EndsWith("ggml-large-v3-turbo.bin", cmd.GpuModelPath);
    }
}
