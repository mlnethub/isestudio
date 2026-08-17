namespace OnToPilot.Ontology;

/// <summary>
/// The three governed RDF layers of a knowledge system. Mirrors the graph
/// partition surfaced by <see cref="KsContext"/>: the TBox (schema), ABox
/// (instances), and Vocabulary (SKOS).
/// </summary>
public enum RdfLayer
{
    /// <summary>Schema / ontology layer (rdfs:Class, owl:ObjectProperty, …).</summary>
    TBox,

    /// <summary>Instance layer (typed individuals + their facts).</summary>
    ABox,

    /// <summary>SKOS vocabulary layer (ConceptScheme + Concept).</summary>
    Vocabulary,
}