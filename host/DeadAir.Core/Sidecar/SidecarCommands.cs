using System.Text.Json.Serialization;
using DeadAir.Core.Config;

namespace DeadAir.Core.Sidecar;

public sealed record ConfigCommand
{
    [JsonPropertyName("cmd")] public string Cmd => "config";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "auto";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("cpu_model")] public string CpuModel { get; init; } = "";
    [JsonPropertyName("mic")] public string Mic { get; init; } = "default";
    [JsonPropertyName("dictionary")] public List<string> Dictionary { get; init; } = new();
    [JsonPropertyName("gpu_server_exe")] public string GpuServerExe { get; init; } = "";
    [JsonPropertyName("gpu_model_path")] public string GpuModelPath { get; init; } = "";
    [JsonPropertyName("gpu_port")] public int GpuPort { get; init; } = 8910;

    public static ConfigCommand From(AppConfig c) => new()
    {
        Engine = c.Asr.Engine,
        Model = c.Asr.GpuModel,
        CpuModel = c.Asr.CpuModel,
        Mic = c.Mic,
        Dictionary = c.Dictionary,
        GpuServerExe = SidecarPathResolver.ResolveAsset(AppContext.BaseDirectory,
            c.Asr.GpuServerExe, ProbeRelative(c.Asr.GpuServerExe)),
        GpuModelPath = SidecarPathResolver.ResolveAsset(AppContext.BaseDirectory,
            c.Asr.GpuModelPath, ProbeRelative(c.Asr.GpuModelPath)),
        GpuPort = c.Asr.GpuPort,
    };

    // Configured defaults are "..\..\tools\whisper\whisper-server.exe" style — the walk-up
    // probe is the same path with leading "..\" segments stripped (i.e. relative to the
    // repo root rather than to whatever depth the exe happens to live at).
    private static string ProbeRelative(string configured) =>
        configured.Replace(".." + Path.DirectorySeparatorChar, "")
                  .Replace(".." + Path.AltDirectorySeparatorChar, "");
}

public sealed record SimpleCommand([property: JsonPropertyName("cmd")] string Cmd);
