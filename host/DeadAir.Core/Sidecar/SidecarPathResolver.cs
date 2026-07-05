namespace DeadAir.Core.Sidecar;

/// <summary>Resolves the sidecar directory and venv python for dev layouts where
/// the exe's depth under the repo root varies (Debug/Release/App vs Tests).</summary>
public static class SidecarPathResolver
{
    /// <returns>(pythonPath, workingDir) — absolute; falls back to the configured
    /// values resolved against baseDir when discovery fails.</returns>
    public static (string Python, string WorkingDir) Resolve(
        string baseDir, string configuredPython, string configuredWorkingDir)
    {
        var cfgWork = Path.GetFullPath(configuredWorkingDir, baseDir);

        if (!configuredPython.Contains(Path.DirectorySeparatorChar) &&
            !configuredPython.Contains(Path.AltDirectorySeparatorChar))
            return (configuredPython, cfgWork); // bare command (e.g. "python") — PATH lookup, no discovery

        var cfgPython = Path.GetFullPath(configuredPython, baseDir);
        if (Directory.Exists(Path.Combine(cfgWork, "asr_sidecar")) && File.Exists(cfgPython))
            return (cfgPython, cfgWork);

        // Walk up from baseDir looking for a 'sidecar' dir containing the package.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sidecar");
            if (Directory.Exists(Path.Combine(candidate, "asr_sidecar")))
            {
                var python = Path.Combine(candidate, ".venv", "Scripts", "python.exe");
                if (File.Exists(python)) return (python, candidate);
            }
        }
        return (cfgPython, cfgWork); // discovery failed — let LaunchAsync surface the error
    }

    /// <summary>Resolves a repo-root asset (GPU server exe / model file) whose configured
    /// path is relative to the exe's own directory. Dev layouts vary in how deep the exe
    /// sits under the repo root (Debug/Release/App vs Tests), so a fixed number of "..\.."
    /// segments in the configured default can miss. If the configured path (resolved
    /// against baseDir) exists, it wins. Otherwise walk up from baseDir looking for
    /// probeRelative (e.g. "tools\whisper\whisper-server.exe") — first hit wins. If nothing
    /// is found, return the configured-resolved value so the caller can surface the error.</summary>
    public static string ResolveAsset(string baseDir, string configured, string probeRelative)
    {
        var cfgResolved = Path.GetFullPath(configured, baseDir);
        if (File.Exists(cfgResolved)) return cfgResolved;

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, probeRelative);
            if (File.Exists(candidate)) return candidate;
        }
        return cfgResolved; // discovery failed — let the sidecar surface the error
    }
}
