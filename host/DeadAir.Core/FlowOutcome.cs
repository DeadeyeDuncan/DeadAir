namespace DeadAir.Core;

/// <summary>How one utterance ended. FlowState cannot carry this: the empty
/// and error paths both land on FlowState.Idle, so a state-keyed UI would
/// caption an ASR error "nothing heard".
///
/// Raised unconditionally. Deciding whether a given outcome should be DRAWN
/// (e.g. suppressing a superseded tail's caption during a live recording) is
/// the App layer's job -- see PillStatus.SuppressTerminal. Do not add
/// ownership or idempotence logic here; two review rounds proved this state
/// machine's _utteranceId cannot express UI lifetime.</summary>
public enum FlowOutcome
{
    Injected,
    NothingHeard,
    Failed,
    TimedOut,
    Interrupted,
}
