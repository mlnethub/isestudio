namespace ISEStudio.Application.Ontology;

/// <summary>
/// Result envelope for a single ontology edit. The <see cref="Iri"/>
/// carries the affected resource IRI (the new class IRI for
/// <c>add_class</c>, the deleted class IRI for <c>delete_class</c>, the
/// axiom-type name for <c>add_axiom</c>, …). The dispatcher / facade
/// is responsible for surfacing the value through the matching
/// FastAPI-shaped response.
/// </summary>
public sealed record OntologyEditResult(string Iri);