using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

/// <summary>
/// Light-weight stand-in for <see cref="KnowledgeSystemEntity"/> used by the
/// ontology services and tests. The .NET <see cref="KnowledgeSystemEntity"/>
/// exposes <c>GraphIri</c> + <c>BaseIri</c> directly; this record mirrors the
/// surface those services depend on so tests can construct one without a
/// database, and so the service methods accept a single argument instead of
/// three strings.
/// </summary>
/// <remarks>
/// Graph derivation mirrors the Python backend:
/// <list type="bullet">
/// <item><c>TBoxGraph</c> &mdash; <c>graphIri.TrimEnd('/')</c> (the KS's
/// schema graph).</item>
/// <item><c>ABoxGraph</c> &mdash; <c>graphIri.TrimEnd('/') + "/abox"</c>
/// (instance graph; instances never land in the schema graph).</item>
/// <item><c>VocabularyGraph</c> &mdash; <c>graphIri.TrimEnd('/') + "/vocabulary"</c>
/// (SKOS ConceptSchemes + Concepts).</item>
/// </list>
/// </remarks>
public sealed record KsContext(
    string GraphIri,
    string BaseIri)
{
    /// <summary>The TBox (schema) graph for this knowledge system.</summary>
    public string TBoxGraph => GraphIri.TrimEnd('/');

    /// <summary>The ABox (instance) graph for this knowledge system.</summary>
    public string ABoxGraph => GraphIri.TrimEnd('/') + "/abox";

    /// <summary>The Vocabulary (SKOS) graph for this knowledge system.</summary>
    public string VocabularyGraph => GraphIri.TrimEnd('/') + "/vocabulary";

    /// <summary>Build from an EF <see cref="KnowledgeSystemEntity"/>.</summary>
    public static KsContext FromEntity(KnowledgeSystemEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new KsContext(entity.GraphIri, entity.BaseIri);
    }
}