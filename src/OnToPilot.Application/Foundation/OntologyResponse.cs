namespace OnToPilot.Application.Foundation;

/// <summary>
/// Protocol-agnostic DTO returned by
/// <see cref="Integration.IIntegrationApiFacade.GetOntologyAsync"/>. Carries
/// the structured TBox for the bound knowledge system. The concrete schema
/// is owned by task 2; this stub keeps the facade shape compiling.
/// </summary>
public sealed record OntologyResponse(IReadOnlyList<OntologyClass> Classes, IReadOnlyList<OntologyProperty> Properties);

public sealed record OntologyClass(string Iri, string? Label);

public sealed record OntologyProperty(string Iri, string? Label);