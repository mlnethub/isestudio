using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ISEStudio.Application.Foundation;

/// <summary>
/// Curated JSON view returned by
/// <see cref="Integration.IIntegrationApiFacade.GetOntologyAsync"/>. The
/// field shape mirrors the Python
/// <c>backend/app/ontology/schema.py::build_view</c> contract
/// line-for-line so the FastAPI frontend consumes the same payload
/// regardless of which backend served it.
/// </summary>
/// <remarks>
/// <para>The <see cref="KnowledgeSystem"/> property is typed as
/// <see cref="object"/> so the same DTO can carry either an internal /
/// published system (<see cref="KnowledgeSystemMeta"/>, primary key is a
/// <see cref="Guid"/>) or an external system
/// (<see cref="ExternalKnowledgeSystemMeta"/>, identifier is the public
/// id string). The polymorphic wire shape &mdash; both branches emit
/// <c>{"id": ..., "name": ..., "base_iri": ..., "release"?: ...}</c>
/// &mdash; is handled by <see cref="KnowledgeSystemMetaConverter"/>,
/// bound through <c>[property: JsonConverter]</c> on this positional
/// property.</para>
/// </remarks>
public sealed record OntologyResponse(
    IReadOnlyList<OntologyClass> Classes,
    IReadOnlyList<OntologyProperty> ObjectProperties,
    IReadOnlyList<OntologyProperty> DataProperties,
    OntologyAxioms Axioms,
    IReadOnlyDictionary<string, string> Labels,
    OntologyStats Stats,
    [property: JsonConverter(typeof(KnowledgeSystemMetaConverter))]
    object? KnowledgeSystem);

public sealed record OntologyAxioms(
    IReadOnlyList<SubclassAxiom> SubclassOf,
    IReadOnlyList<PairAxiom> DisjointWith,
    IReadOnlyList<PairAxiom> EquivalentClass);

public sealed record SubclassAxiom(string Sub, string Super);

public sealed record PairAxiom(string A, string B);

public sealed record OntologyStats(int ClassCount, int PropertyCount, int AxiomCount);

/// <summary>
/// Meta block for an internal or published knowledge system. The
/// primary-key <see cref="Id"/> is a <see cref="Guid"/>; the optional
/// <see cref="Release"/> is the deployment-row slug, present only for
/// published-release views.
/// </summary>
public sealed record KnowledgeSystemMeta(
    [property: JsonInclude] Guid Id,
    [property: JsonInclude] string Name,
    [property: JsonInclude] string BaseIri,
    [property: JsonInclude] string? Release);

/// <summary>
/// Meta block for an external knowledge system. The wire field
/// <c>id</c> carries the human-readable <see cref="PublicId"/> string
/// (not a <see cref="Guid"/>) &mdash; the C# distinction is invisible
/// to the frontend, which always reads <c>id: string</c>.
/// </summary>
public sealed record ExternalKnowledgeSystemMeta(
    [property: JsonInclude] string PublicId,
    [property: JsonInclude] string Name,
    [property: JsonInclude] string BaseIri);

public sealed record OntologyClass(string Iri, string? Label)
{
    public string Local { get; init; } = "";
    public string Comment { get; init; } = "";
    public IReadOnlyList<string> Superclasses { get; init; } = Array.Empty<string>();
}

public sealed record OntologyProperty(string Iri, string? Label)
{
    public string Local { get; init; } = "";
    public string Comment { get; init; } = "";
    public string? Domain { get; init; }
    public string? DomainLabel { get; init; }
    public string? Range { get; init; }
    public string? RangeLabel { get; init; }
    public IReadOnlyList<string> DomainMembers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RangeMembers { get; init; } = Array.Empty<string>();
}