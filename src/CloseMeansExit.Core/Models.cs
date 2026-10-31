namespace CloseMeansExit.Core;

public enum CloseEvidence
{
    StandardCloseButton,
    AltF4,
    CustomTitleBarZone,
    WindowDisappeared
}

public sealed record ProcessIdentity(int Pid, long StartTimeUtcTicks, string ExecutablePath);

public sealed record AppRule
{
    public string ExecutablePath { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool IncludeChildren { get; init; }
    public bool UseCustomCloseZone { get; init; }
}

public sealed record CloseObservation(
    ProcessIdentity Process,
    string WindowTitle,
    CloseEvidence Evidence,
    DateTimeOffset At)
{
    public CloseWindowSource? SourceWindow { get; init; }
}

public sealed record CloseWindowSource(nint Handle, ProcessIdentity Owner);

public sealed record ProcessSnapshot(
    bool IsAlive,
    bool IdentityMatches,
    bool HasVisibleWindows,
    bool Reliable,
    long WorkingSetBytes)
{
    public string? Diagnostic { get; init; }
    public bool OnlySourceWindowVisible { get; init; }
}

public sealed record WindowEntry(nint Handle, ProcessIdentity Process, string Title, long WorkingSetBytes);

public enum DecisionKind { Wait, Ignore, Candidate, Terminate }
public sealed record PolicyDecision(DecisionKind Kind, string Reason);
