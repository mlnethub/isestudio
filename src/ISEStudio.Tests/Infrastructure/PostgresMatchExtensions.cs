using ISEStudio.Ontology;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoQuad = Oxigraph.Quad;

namespace ISEStudio.Tests.Infrastructure;

/// <summary>
/// Test-side helper: <see cref="IRdfStatementRepository"/> is the canonical
/// read path now that the workspace has migrated off Oxigraph, but a lot
/// of API-level tests still call <c>store.Match(...)</c> against the
/// legacy <c>StoreWrapper</c>. The extension methods below let a test
/// scope a query to a single (knowledge system, layer) and assert against
/// the authoritative PostgreSQL rows.
/// </summary>
public static class PostgresMatchExtensions
{
    /// <summary>
    /// Match quads in the requested layer by optional subject /
    /// predicate / object / graph filters — drop-in replacement for
    /// <c>StoreWrapper.Match(...)</c> against the PostgreSQL workspace
    /// table.
    /// </summary>
    public static List<OntoQuad> MatchPostgres(
        this ISEStudio.Infrastructure.Persistence.ISEStudioDbContext db,
        Guid knowledgeSystemId,
        RdfLayer layer,
        string? graphIri = null,
        string? subjectIri = null,
        string? predicateIri = null,
        string? objectIri = null)
    {
        var statements = new PostgresRdfStatementRepository(db)
            .ListAsync(knowledgeSystemId, layer.ToString())
            .GetAwaiter().GetResult();
        return statements
            .Where(s => graphIri is null || s.GraphIri == graphIri)
            .Where(s => subjectIri is null || (s.Subject is RdfIri iri && iri.Value == subjectIri))
            .Where(s => predicateIri is null || s.PredicateIri == predicateIri)
            .Where(s => objectIri is null || (s.Object is RdfIri iri && iri.Value == objectIri))
            .Select(ToQuad).ToList();
    }

    private static OntoQuad ToQuad(RdfStatement statement)
    {
        var graph = new OntoNamedNode(statement.GraphIri ?? throw new InvalidOperationException("RDF graph is required"));
        return new OntoQuad(ToSubject(statement.Subject), new OntoNamedNode(statement.PredicateIri), ToObject(statement.Object), graph);
    }

    private static Oxigraph.INamedOrBlankNode ToSubject(RdfTerm term) => term switch
    {
        RdfIri iri => new OntoNamedNode(iri.Value),
        RdfBlankNode blank => new Oxigraph.BlankNode(blank.Id),
        _ => throw new InvalidOperationException("RDF subject must be an IRI or blank node"),
    };

    private static Oxigraph.ITerm ToObject(RdfTerm term) => term switch
    {
        RdfIri iri => new OntoNamedNode(iri.Value),
        RdfBlankNode blank => new Oxigraph.BlankNode(blank.Id),
        RdfLiteral literal => new Oxigraph.Literal(literal.Value, literal.Language,
            literal.Datatype is null ? null : new OntoNamedNode(literal.Datatype)),
        _ => throw new InvalidOperationException($"Unsupported RDF term: {term.GetType().Name}"),
    };
}
