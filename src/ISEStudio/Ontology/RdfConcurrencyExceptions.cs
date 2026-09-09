namespace ISEStudio.Ontology;

/// <summary>
/// Raised when a caller cannot acquire a per-graph write or read lease within
/// the configured wait window. Maps cleanly to HTTP 409 at the API layer.
/// </summary>
public sealed class GraphWriteConflictException : Exception
{
    public GraphWriteConflictException(string message) : base(message) { }
    public GraphWriteConflictException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// Constructor for the brief's "抽取进行中的修改返回 409" path. The
    /// middleware surfaces <see cref="JobId"/> in the
    /// <c>{"detail": { "error": "...", "job_id": "..." }}</c> envelope
    /// so clients can poll the job row that blocked the mutation.
    /// </summary>
    public GraphWriteConflictException(string message, Guid jobId) : base(message)
    {
        JobId = jobId;
    }

    /// <summary>The extraction job whose in-flight status blocked the mutation.</summary>
    public Guid? JobId { get; }
}

/// <summary>
/// Raised when a delete is refused because some other row still references
/// the target (typical case: a knowledge system or system config still
/// points at a provider row). Maps to HTTP 409 with a plain-string
/// <c>{"detail": "..."}</c> envelope at the API layer — distinct from
/// <see cref="GraphWriteConflictException"/>, which carries the structured
/// <c>{"detail": { "error": "...", "job_id": "..." }}</c> shape mandated
/// by the brief's extraction-in-progress rule.
/// </summary>
/// <remarks>
/// <para>The exception is intentionally generic over the referenced
/// resource kind so future callers (vocabulary schemes, ABox
/// individuals, etc.) can reuse it without inventing a sibling type per
/// case.</para>
/// </remarks>
public sealed class ResourceInUseException : Exception
{
    public ResourceInUseException(string message) : base(message) { }
    public ResourceInUseException(string message, Exception inner) : base(message, inner) { }
}
