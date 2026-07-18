using System.Text.Json.Serialization;

namespace DeadAir.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter<CleanupMode>))]
public enum CleanupMode { Faithful, Polished }

public sealed class AppConfig
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public string ModeToggleHotkey { get; set; } = "Ctrl+Alt+M";
    public AsrConfig Asr { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public CleanupConfig Cleanup { get; set; } = new();
    public PromptsConfig Prompts { get; set; } = new();
    public List<string> Dictionary { get; set; } = new();
    public string Mic { get; set; } = "default";
    public InjectConfig Inject { get; set; } = new();
    public PillConfig Pill { get; set; } = new();
    public SidecarLaunchConfig Sidecar { get; set; } = new();
}

public sealed class HotkeyConfig
{
    public string Key { get; set; } = "RControl";
    public string Mode { get; set; } = "hold";
}

public sealed class AsrConfig
{
    public string Engine { get; set; } = "auto"; // auto | gpu | cpu
    public string GpuModel { get; set; } = "large-v3-turbo";
    public string CpuModel { get; set; } = "small";
    public string GpuServerExe { get; set; } = @"..\..\tools\whisper\whisper-server.exe";
    public string GpuModelPath { get; set; } = @"..\..\models\ggml-large-v3-turbo.bin";
    public int GpuPort { get; set; } = 8910;
    public bool Partials { get; set; } = true;
    public int PartialIntervalMs { get; set; } = 600;
    public int PartialMinMs { get; set; } = 700;
    public int PartialWindowSeconds { get; set; } = 30;
}

public sealed class OllamaConfig
{
    // 127.0.0.1, not "localhost": Windows resolves localhost to ::1 first, Ollama
    // binds IPv4 loopback only, and each refused connect costs a ~2s Winsock
    // SYN-retry. OllamaClient also rewrites a configured "localhost" defensively.
    public string Url { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen2.5:7b";
    public int NumCtx { get; set; } = 8192;
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 20;
    public string KeepAlive { get; set; } = "30m";
}

public sealed class CleanupConfig
{
    public CleanupMode Mode { get; set; } = CleanupMode.Faithful;
    public int SkipGuardChars { get; set; } = 50;
    // "English" (any casing) or blank = translation off. Free-form otherwise —
    // the value is substituted into Prompts.TranslationTemplate, so any
    // language name the LLM knows works ("Spanish", "French", ...).
    public string OutputLanguage { get; set; } = "English";

    [JsonIgnore]
    public bool TranslationActive
    {
        get
        {
            var lang = OutputLanguage?.Trim();
            return !string.IsNullOrEmpty(lang) &&
                !string.Equals(lang, "English", StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class PromptsConfig
{
    public string Faithful { get; set; } =
        "You clean raw speech-to-text transcripts. Remove filler words (um, uh, like, you know). " +
        "Fix punctuation, capitalization, and light grammar. If the speaker self-corrects, keep only " +
        "the corrected version. Preserve the speaker's meaning, wording, and tone. Do NOT add, infer, " +
        "summarize, reword, or answer anything. Preserve technical terms, names, commands, and file " +
        "paths exactly. Output ONLY the cleaned transcript with no preamble.";

    public string Polished { get; set; } =
        "You clean and lightly polish raw speech-to-text transcripts. Remove filler words, fix " +
        "punctuation/capitalization/grammar, keep only self-corrected versions, and smooth awkward " +
        "or run-on phrasing into clear, natural sentences. Preserve the speaker's meaning, intent, " +
        "and tone — do NOT add new information, summarize, or answer anything. Preserve technical " +
        "terms, names, commands, and file paths exactly. Output ONLY the polished transcript with " +
        "no preamble.";

    public string TranslationTemplate { get; set; } =
        "After applying the rules above, render the transcript in {language}. " +
        "The translation must be {style}. Keep technical terms, product names, " +
        "commands, file paths, and any listed dictionary terms in their original " +
        "language. Output ONLY the {language} text with no preamble.";
}

public sealed class InjectConfig
{
    public string Method { get; set; } = "auto"; // auto | clipboard | sendinput
    public string PasteHotkey { get; set; } = "Ctrl+V";
    public int RestoreClipboardDelayMs { get; set; } = 150;
}

public sealed class PillConfig
{
    // Nebula dials (host-only, never sent to the sidecar). Window clamps at
    // apply: 0.5-8 / 0-1.5 / 0-4.
    public double FanGain { get; set; } = 3.0;      // voice sensitivity of the fan
    public double Wiggle { get; set; } = 0.6;       // turbulence depth at full voice
    public double WiggleSpeed { get; set; } = 1.0;  // traveling-wave speed multiplier
}

public sealed class SidecarLaunchConfig
{
    public string Python { get; set; } = @"..\..\sidecar\.venv\Scripts\python.exe";
    public string Args { get; set; } = "-m asr_sidecar";
    public string WorkingDir { get; set; } = @"..\..\sidecar";
}
