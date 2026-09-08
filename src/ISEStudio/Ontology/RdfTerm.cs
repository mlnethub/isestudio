namespace ISEStudio.Ontology;

public abstract record RdfTerm;

public sealed record RdfIri(string Value) : RdfTerm;

public sealed record RdfBlankNode(string Id) : RdfTerm;

public sealed record RdfLiteral(string Value, string? Language = null, string? Datatype = null) : RdfTerm;
