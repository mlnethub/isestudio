using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Application.Sparql;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Ontology;
using System.Text.RegularExpressions;

namespace ISEStudio.Sparql;

/// <summary>
/// Concrete <see cref="ISparqlQueryExecutor"/> backed by the workspace
/// <see cref="StoreWrapper"/> and the EF <see cref="ISEStudioDbContext"/>.
/// Resolves the public-id to a <see cref="KsContext"/> and binds the
/// SPARQL execution to its three graphs so cross-KS reads are
/// structurally impossible.
/// </summary>
public sealed class SparqlQueryExecutor : ISparqlQueryExecutor
{
    private readonly ISEStudioDbContext _db;
    private readonly IRdfStatementRepository _statements;

    public SparqlQueryExecutor(ISEStudioDbContext db, IRdfStatementRepository statements)
    {
        _db = db;
        _statements = statements;
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

        // Read-only policy enforcement lives here (the SPARQL boundary) so
        // both the HTTP path through PublishedController and the MCP path
        // through ISEStudioMcpTools are protected identically. The
        // controller pre-validates for an early 400; this is the canonical
        // safety net.
        var policy = ISEStudio.Api.ReadOnlySparqlPolicy.Validate(sparql);
        if (policy is ISEStudio.Api.ReadOnlySparqlPolicyResult.Reject rejected)
        {
            throw new ISEStudio.Api.ValidationException(rejected.Reason);
        }
        var normalised = ((ISEStudio.Api.ReadOnlySparqlPolicyResult.Allow)policy).Normalised;

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PublicId == publicId, cancellationToken)
            .ConfigureAwait(false);
        if (ks is null)
            throw new KeyNotFoundException($"Knowledge system '{publicId}' not found.");

        var capped = Math.Clamp(maxRows, 1, 10_000);
        var sparqlWithLimit = EnsureLimit(normalised, capped);
        var statements = await _statements.ListAsync(ks.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ExecuteSupportedQuery(sparqlWithLimit, statements, capped);
    }

    /// <summary>
    /// Append a <c>LIMIT N</c> clause if the SPARQL has none. SPARQL is
    /// case-insensitive and tolerates whitespace; trailing semicolons are
    /// stripped (SPARQL syntax does not accept them) and the <c>LIMIT</c>
    /// keyword is matched case-insensitively. If absent, <c>LIMIT N</c> is
    /// appended at the end.
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
            return trimmed;
        }
        return trimmed + " LIMIT " + maxRows;
    }

    private static QueryResponse ExecuteSupportedQuery(
        string sparql,
        IReadOnlyList<RdfStatement> statements,
        int maxRows)
    {
        var ask = Regex.IsMatch(sparql, @"^\s*ASK\b", RegexOptions.IgnoreCase);
        var select = Regex.Match(sparql,
            @"^\s*SELECT\s+(?<vars>.*?)\s+WHERE\s*\{(?<pattern>.*?)\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!ask && !select.Success)
            throw new ISEStudio.Api.ValidationException("Only SELECT/ASK with one triple pattern is supported by the PostgreSQL query executor.");

        var body = ask ? Regex.Match(sparql, @"\{(?<pattern>.*?)\}", RegexOptions.Singleline) : select;
        var parts = Regex.Matches(body.Groups["pattern"].Value, "(?:<[^>]*>|\\?[A-Za-z_][\\w-]*|\"(?:[^\"\\\\]|\\\\.)*\")")
            .Select(match => match.Value).ToArray();
        if (parts.Length != 3)
            throw new ISEStudio.Api.ValidationException("The PostgreSQL query executor supports exactly one triple pattern.");

        var matches = statements.Where(statement => Matches(statement, parts)).Take(maxRows).ToList();
        if (ask)
            return new QueryResponse(new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["boolean"] = matches.Count > 0 } });

        var variables = select.Groups["vars"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.StartsWith("?", StringComparison.Ordinal)).ToArray();
        var rows = matches.Select(statement =>
        {
            var values = Terms(statement);
            return (IReadOnlyDictionary<string, object?>)variables
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(variable => variable[1..], variable => (object?)values[Array.IndexOf(parts, variable)], StringComparer.OrdinalIgnoreCase);
        }).ToList();
        return new QueryResponse(rows);
    }

    private static bool Matches(RdfStatement statement, string[] pattern)
    {
        var values = Terms(statement);
        return pattern.Select((token, index) => (token, index)).All(item =>
            item.token.StartsWith("?", StringComparison.Ordinal)
            || string.Equals(item.token, $"<{values[item.index]}>", StringComparison.Ordinal)
            || string.Equals(item.token, $"\"{values[item.index]}\"", StringComparison.Ordinal));
    }

    private static string[] Terms(RdfStatement statement) =>
        [TermText(statement.Subject), statement.PredicateIri, TermText(statement.Object)];

    private static string TermText(RdfTerm term) => term switch
    {
        RdfIri iri => iri.Value,
        RdfBlankNode blank => "_:" + blank.Id,
        RdfLiteral literal => literal.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(term)),
    };
}
