namespace ISEStudio.Ontology;

public sealed record RdfStatement(
    RdfTerm Subject,
    string PredicateIri,
    RdfTerm Object,
    string? GraphIri = null);
