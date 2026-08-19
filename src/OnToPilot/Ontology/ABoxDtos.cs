namespace OnToPilot.Ontology;

/// <summary>
/// A single class entry for the ABox sidebar. <see cref="Count"/> is the
/// number of individuals of this class in the ABox graph (zero when the
/// class has no instances yet).
/// </summary>
public sealed record ClassEntry(
    string Iri,
    string Label,
    int Count);

/// <summary>Wire envelope for <c>GET /abox/classes</c>.</summary>
public sealed record ClassesOut(
    IReadOnlyList<ClassEntry> Classes,
    int Total);

/// <summary>
/// A single individual row for <c>GET /abox/individuals</c>. The
/// <see cref="Label"/> falls back to the local IRI fragment when the
/// ABox graph doesn't carry an <c>rdfs:label</c> yet.
/// </summary>
public sealed record IndividualListItem(
    string Iri,
    string Label,
    IReadOnlyList<string> TypeIris);

/// <summary>Wire envelope for <c>GET /abox/individuals</c>.</summary>
public sealed record IndividualsOut(
    IReadOnlyList<IndividualListItem> Items,
    int Total);

/// <summary>
/// Lightweight <c>(iri, label)</c> for type / property / target lookups.
/// The <see cref="Label"/> falls back to the local IRI fragment when no
/// <c>rdfs:label</c> is recorded.
/// </summary>
public sealed record LabeledIri(
    string Iri,
    string Label);

/// <summary>
/// One object-property assertion. Mirrors the Python
/// <c>ind["object_assertions"]</c> row.
/// </summary>
public sealed record ObjectAssertionOut(
    string Prop,
    string PropLabel,
    string Target,
    string TargetLabel,
    IReadOnlyList<object> Sources);

/// <summary>
/// One data-property assertion. Mirrors the Python
/// <c>ind["data_assertions"]</c> row.
/// </summary>
public sealed record DataAssertionOut(
    string Prop,
    string PropLabel,
    string Value,
    string? Datatype,
    IReadOnlyList<object> Sources);

/// <summary>
/// Full individual envelope. Mirrors the Python
/// <c>backend/app/ontology/abox.py::get_individual</c> shape minus the
/// <c>sources</c> arrays attached by the Python side from
/// <c>abox_provenance.sources_for</c>. The .NET port defers the
/// provenance attachment to a later slice (ABoxProvenanceService wire-up).
/// </summary>
public sealed record IndividualOut(
    string Iri,
    string Label,
    IReadOnlyList<LabeledIri> Types,
    IReadOnlyList<ObjectAssertionOut> ObjectAssertions,
    IReadOnlyList<DataAssertionOut> DataAssertions);

/// <summary>Return shape for <c>POST /abox/individuals</c> (created individual).</summary>
public sealed record CreateIndividualRequest(
    string Label,
    string ClassIri);

/// <summary>Return shape for <c>POST /abox/individuals/delete</c>.</summary>
public sealed record IndividualRef(string Iri);

/// <summary>Return shape for <c>POST /abox/individuals/delete</c>.</summary>
public sealed record DeleteIndividualResponse(int Removed);

/// <summary>
/// Request body for <c>POST /abox/assertions</c> and
/// <c>POST /abox/assertions/delete</c>. Mirrors the Python
/// <c>backend/app/api/abox.py::Assertion</c> Pydantic model. The
/// <see cref="Kind"/> discriminator picks between object (uses
/// <see cref="Target"/>) and data (uses <see cref="Value"/> +
/// optional <see cref="Datatype"/>) handling.
/// </summary>
public sealed record AssertionRequest(
    string Subject,
    string Prop,
    string Kind,
    string? Target,
    string? Value,
    string? Datatype);