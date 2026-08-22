using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Sparql;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Ontology;
using Oxigraph;
using OntoNamedNode = Oxigraph.NamedNode;

namespace OnToPilot.Sparql;

/// <summary>
/// Concrete <see cref="ISparqlQueryExecutor"/> backed by the workspace
/// <see cref="StoreWrapper"/> and the EF <see cref="OnToPilotDbContext"/>.
/// Resolves the public-id to a <see cref="KsContext"/> and binds the
/// SPARQL execution to its three graphs so cross-KS reads are
/// structurally impossible.
/// </summary>
public sealed class SparqlQueryExecutor : ISparqlQueryExecutor
{
    private readonly OnToPilotDbContext _db;
    private readonly StoreWrapper _store;

    public SparqlQueryExecutor(OnToPilotDbContext db, StoreWrapper store)
    {
        _db = db;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<QueryResponse> ExecuteAsync(
        string publicId,
        string sparql,
        int maxRows,
        TokenPrincipal token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicId))
            throw new KeyNotFoundException("public_id is required.");
        if (string.IsNullOrWhiteSpace(sparql))
            throw new OnToPilot.Api.ValidationException("Query body is required.");

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
            throw new KeyNotFoundException($"Knowledge system '{publicId}' not found.");

        var ctx = KsContext.FromEntity(ks);
        var options = new QueryOptions
        {
            DefaultGraphs = new IGraphName[]
            {
                new OntoNamedNode(ctx.TBoxGraph),
                new OntoNamedNode(ctx.ABoxGraph),
                new OntoNamedNode(ctx.VocabularyGraph),
            },
        };

        var capped = Math.Clamp(maxRows, 1, 10_000);
        var sparqlWithLimit = EnsureLimit(sparql, capped);
        var rows = await _store.QueryAsync(sparqlWithLimit, options, cancellationToken)
            .ConfigureAwait(false);

        return new QueryResponse(rows);
    }

    /// <summary>
    /// Append a <c>LIMIT N</c> clause if the SPARQL has none. SPARQL is
    /// case-insensitive and tolerates whitespace before the trailing
    /// semicolon; we match the trailing <c>LIMIT</c> keyword ignoring case
    /// and, if absent, append <c>LIMIT N</c> at the end.
    /// </summary>
    internal static string EnsureLimit(string sparql, int maxRows)
    {
        var trimmed = sparql.TrimEnd().TrimEnd(';').TrimEnd();
        // Look for " LIMIT <int>" near the end; if absent, append.
        // Simple case-insensitive substring search; Oxigraph will reject
        // malformed queries upstream so a missed LIMIT is benign.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                trimmed, @"\bLIMIT\s+\d+(\s+OFFSET\s+\d+)?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return trimmed + ";";
        }
        return trimmed + " LIMIT " + maxRows + ";";
    }
}
