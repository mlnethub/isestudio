using OnToPilot.Application.Foundation;

namespace OnToPilot.Application.Sparql;

/// <summary>
/// Bounded read-only SPARQL executor over a knowledge system's three
/// workspace graphs (TBox / ABox / Vocabulary). Implementations live in
/// the concrete project; this interface keeps the facade protocol-agnostic
/// and prevents <c>OnToPilot.Application</c> from referencing the EF
/// infrastructure layer.
/// </summary>
public interface ISparqlQueryExecutor
{
    /// <summary>
    /// Execute <paramref name="sparql"/> against the knowledge system
    /// identified by <paramref name="publicId"/>. The query must already
    /// have passed <see cref="OnToPilot.Api.ReadOnlySparqlPolicy.Validate"/>;
    /// the executor does not re-check the form. Results are capped at
    /// <paramref name="maxRows"/>; queries without a LIMIT clause have
    /// one appended before execution.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when no knowledge system matches <paramref name="publicId"/>.
    /// </exception>
    /// <exception cref="OnToPilot.Api.ValidationException">
    /// Thrown when <paramref name="sparql"/> is empty or whitespace.
    /// </exception>
    Task<QueryResponse> ExecuteAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken);
}
