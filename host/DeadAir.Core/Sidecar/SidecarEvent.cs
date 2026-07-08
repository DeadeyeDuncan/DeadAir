using System.Text.Json.Serialization;

namespace DeadAir.Core.Sidecar;

public sealed record SidecarEvent
{
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("ms")] public int? Ms { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("where")] public string? Where { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("samples")] public double[]? Samples { get; init; }
    [JsonPropertyName("seq")] public int? Seq { get; init; }
}
