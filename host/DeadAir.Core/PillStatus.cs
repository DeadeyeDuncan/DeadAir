using DeadAir.Core.Config;

namespace DeadAir.Core;

/// <summary>A pill caption and whether it self-dismisses.</summary>
public readonly record struct PillCaption(string Text, bool Dismiss);

/// <summary>Maps flow states and outcomes to pill captions, and decides when a
/// terminal caption must be withheld. Pure and WPF-free so it is unit-testable
/// headless, matching ScopeGeometry.</summary>
public static class PillStatus
{
    /// <summary>Caption for an in-flight state, or null when the state carries
    /// no caption: Recording is the scope visual, Idle is owned by the terminal
    /// outcome caption, and Cleaning is captioned from CleaningStarted.</summary>
    public static PillCaption? ForState(FlowState state)
        => state switch
        {
            FlowState.Transcribing => new PillCaption("transcribing…", false),
            FlowState.Injecting => new PillCaption("injecting…", false),
            _ => null,
        };

    /// <summary>Caption for the cleanup phase, from the values the operation
    /// was submitted with.</summary>
    public static PillCaption ForCleaning(CleanupMode mode, bool translating)
        => new(translating ? "translating…"
                : mode == CleanupMode.Polished ? "polishing…" : "cleaning…",
               false);

    /// <summary>Caption for a finished utterance. Always self-dismisses.</summary>
    public static PillCaption ForOutcome(FlowOutcome outcome)
        => outcome switch
        {
            FlowOutcome.Injected => new PillCaption("sent", true),
            FlowOutcome.NothingHeard => new PillCaption("nothing heard", true),
            FlowOutcome.TimedOut => new PillCaption("timed out", true),
            FlowOutcome.Interrupted => new PillCaption("interrupted", true),
            _ => new PillCaption("failed", true),
        };

    /// <summary>True when a terminal caption must NOT be drawn: a superseded
    /// utterance's tail can still report an outcome while a NEW recording is
    /// live, and drawing it would overwrite that recording's scope and arm a
    /// dismissal that retracts its pill.</summary>
    public static bool SuppressTerminal(FlowState lastState)
        => lastState == FlowState.Recording;
}
