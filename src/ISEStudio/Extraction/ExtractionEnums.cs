namespace ISEStudio.Extraction;

/// <summary>
/// Live stage of an extraction run. Serialized into
/// <c>extractionjob.phase</c> via <see cref="ExtractionWire.ToWire(ExtractionPhase)"/>
/// using the Python backend's lowercase strings.
/// </summary>
public enum ExtractionPhase
{
    /// <summary>Schema (TBox) axioms are being extracted and merged.</summary>
    TBox,

    /// <summary>Instances (ABox) are being extracted and merged.</summary>
    ABox,

    /// <summary>Conflict queue re-sync before the agents look at it.</summary>
    Conflicts,

    /// <summary>Agentic isolated-class attach (structure agent pass).</summary>
    Structure,

    /// <summary>Deterministic SKOS terminology sync (never fails the run).</summary>
    Terminology,

    /// <summary>Counters, prompt snapshot, and timestamps are being written.</summary>
    Finalizing,
}

/// <summary>
/// Lifecycle of an <see cref="Infrastructure.Persistence.Entities.ExtractionJobEntity"/>.
/// Serialized into <c>extractionjob.status</c>; the same four strings
/// <see cref="Infrastructure.Startup.StaleJobRecoveryService"/> recognises.
/// </summary>
public enum JobStatus
{
    /// <summary>Row created, background work not started yet.</summary>
    Pending,

    /// <summary>Background work in flight.</summary>
    Running,

    /// <summary>Terminal: every phase finished.</summary>
    Completed,

    /// <summary>Terminal: a phase threw; RDF writes for that phase were reverted.</summary>
    Failed,
}

/// <summary>
/// Wire-format mapping between the .NET enums and the lowercase strings the
/// Python backend persisted (and that the existing Postgres rows, the
/// <c>StaleJobRecoveryService</c>, and the REST clients still expect).
/// </summary>
public static class ExtractionWire
{
    /// <summary>Job kind: schema extraction only.</summary>
    public const string KindTBox = "tbox";

    /// <summary>Job kind: instance extraction only.</summary>
    public const string KindABox = "abox";

    /// <summary>Job kind: schema then instances in a single run.</summary>
    public const string KindBoth = "both";

    /// <summary>Persisted form of <paramref name="phase"/>.</summary>
    public static string ToWire(this ExtractionPhase phase) => phase switch
    {
        ExtractionPhase.TBox => "tbox",
        ExtractionPhase.ABox => "abox",
        ExtractionPhase.Conflicts => "conflicts",
        ExtractionPhase.Structure => "structure",
        ExtractionPhase.Terminology => "terminology",
        ExtractionPhase.Finalizing => "finalizing",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown extraction phase."),
    };

    /// <summary>Persisted form of <paramref name="status"/>.</summary>
    public static string ToWire(this JobStatus status) => status switch
    {
        JobStatus.Pending => "pending",
        JobStatus.Running => "running",
        JobStatus.Completed => "completed",
        JobStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    /// <summary>Whether <paramref name="status"/> is one the job will never leave.</summary>
    public static bool IsTerminal(string status) =>
        status == JobStatus.Completed.ToWire() || status == JobStatus.Failed.ToWire();
}
