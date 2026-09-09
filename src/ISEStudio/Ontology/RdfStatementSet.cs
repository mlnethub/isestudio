namespace ISEStudio.Ontology;

/// <summary>
/// Pure functional operations over <see cref="RdfStatement"/> collections.
/// Used by importers, exporters, and PostgreSQL repository methods so that
/// the merge / replace / diff logic has one implementation that all call
/// sites share. No IO, no state — every operation is total over its
/// inputs.
/// </summary>
public static class RdfStatementSet
{
    /// <summary>Combine current and incoming statements, keeping the union
    /// of distinct (subject, predicate, object, graph) tuples.</summary>
    public static IReadOnlyList<RdfStatement> Merge(
        IEnumerable<RdfStatement> current,
        IEnumerable<RdfStatement> incoming) =>
        current.Concat(incoming).Distinct().ToList();

    /// <summary>
    /// Remove every statement currently in <paramref name="graphIri"/> and
    /// append the replacement statements (attaching the same graph IRI to
    /// each one). Statements outside the named graph are left untouched.
    /// </summary>
    public static IReadOnlyList<RdfStatement> ReplaceGraph(
        IEnumerable<RdfStatement> current,
        string graphIri,
        IEnumerable<RdfStatement> replacement) =>
        current.Where(s => s.GraphIri != graphIri)
            .Concat(replacement.Select(s => s with { GraphIri = graphIri }))
            .Distinct()
            .ToList();

    /// <summary>Drop every statement that matches the (predicate, graph)
    /// predicate filter, returning the surviving statements.</summary>
    public static IReadOnlyList<RdfStatement> Where(
        IEnumerable<RdfStatement> current,
        Func<RdfStatement, bool> predicate) =>
        current.Where(predicate).ToList();

    /// <summary>Compute the symmetric difference between two statement
    /// sets — the union minus the intersection — as ordered
    /// (added, removed) tuples. Each tuple is deterministic for the same
    /// inputs.</summary>
    public static (IReadOnlyList<RdfStatement> Added, IReadOnlyList<RdfStatement> Removed)
        Diff(IEnumerable<RdfStatement> before, IEnumerable<RdfStatement> after)
    {
        var beforeSet = new HashSet<RdfStatement>(before);
        var afterSet = new HashSet<RdfStatement>(after);
        var added = afterSet.Except(beforeSet).OrderBy(StatementKey, StringComparer.Ordinal).ToList();
        var removed = beforeSet.Except(afterSet).OrderBy(StatementKey, StringComparer.Ordinal).ToList();
        return (added, removed);
    }

    private static string StatementKey(RdfStatement statement) =>
        $"{(statement.Subject is RdfIri si ? si.Value : ((RdfBlankNode)statement.Subject).Id)}|{statement.PredicateIri}|{TermKey(statement.Object)}|{statement.GraphIri}";

    private static string TermKey(RdfTerm term) => term switch
    {
        RdfIri iri => "<" + iri.Value + ">",
        RdfBlankNode blank => "_:" + blank.Id,
        RdfLiteral literal => "\"" + literal.Value + "\"" + (literal.Language is not null ? "@" + literal.Language
            : literal.Datatype is not null ? "^^<" + literal.Datatype + ">" : "^^<http://www.w3.org/2001/XMLSchema#string>"),
        _ => term.ToString() ?? string.Empty,
    };
}