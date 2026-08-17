using OnToPilot.Ontology;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Extraction;

/// <summary>
/// Production <see cref="IExtractionMerger"/>. Writes through
/// <see cref="StoreWrapper"/> primitives only — see the locking contract on
/// <see cref="IExtractionMerger"/> for why it must not open its own capture.
/// </summary>
/// <remarks>
/// <para>Counters are computed by probing the store before each write, so a
/// re-run over the same chunk reports zero additions instead of double
/// counting (Oxigraph collapses identical quads, making the write itself
/// idempotent).</para>
/// <para>Unresolvable references never fail the merge: an unknown class label
/// increments the <c>unknown_classes</c> histogram, an unknown property label
/// drops the assertion, and a relation whose target was not seen in the same
/// chunk is counted as pending for the manual-resolution queue. This mirrors
/// the Python workers, where a single bad candidate must not lose the rest of
/// the chunk.</para>
/// </remarks>
public sealed class ExtractionMerger : IExtractionMerger
{
    private readonly StoreWrapper _store;

    public ExtractionMerger(StoreWrapper store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public ExtractionMergeResult MergeTBox(KsContext ks, TBoxDelta delta)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.IsEmpty) return ExtractionMergeResult.Empty;

        var quads = SchemaBuilder.BuildMutation(ks.BaseIri, delta.ToMutation(), ks.TBoxGraph);

        var classesAdded = 0;
        var propertiesAdded = 0;
        var axiomsAdded = 0;
        var provenance = new List<string>();

        foreach (var quad in quads)
        {
            if (_store.ContainsQuad(quad)) continue;

            var predicate = quad.Predicate.Value;
            if (predicate == Vocabulary.RdfType.Value && quad.Object is OntoNamedNode type)
            {
                if (type.Value == Vocabulary.OwlClass.Value) classesAdded++;
                else if (type.Value == Vocabulary.OwlObjectProperty.Value ||
                         type.Value == Vocabulary.OwlDatatypeProperty.Value)
                {
                    propertiesAdded++;
                }
            }
            else if (predicate == Vocabulary.RdfsSubClassOf.Value ||
                     predicate == Vocabulary.OwlDisjointWith.Value ||
                     predicate == Vocabulary.OwlEquivalentClass.Value)
            {
                axiomsAdded++;
                provenance.Add(StatementProvenanceService.TripleKey(
                    new StatementTriple(TermText(quad.Subject), predicate, TermText(quad.Object))));
            }
        }

        _store.AddQuads(new OntoNamedNode(ks.TBoxGraph), quads);

        return new ExtractionMergeResult(
            classesAdded,
            propertiesAdded,
            axiomsAdded,
            IndividualsAdded: 0,
            AssertionsAdded: 0,
            PendingAdded: 0,
            new Dictionary<string, int>(StringComparer.Ordinal),
            provenance);
    }

    /// <inheritdoc />
    public ExtractionMergeResult MergeABox(KsContext ks, ABoxDelta delta)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.IsEmpty) return ExtractionMergeResult.Empty;

        var view = SchemaBuilder.BuildView(ks.TBoxGraph, _store);
        var classIndex = BuildIndex(view.Classes.Select(c => (c.Label, c.Iri)));
        var objectProperties = BuildIndex(view.ObjectProperties.Select(p => (p.Label, p.Iri)));
        var dataProperties = BuildIndex(view.DataProperties.Select(p => (p.Label, p.Iri)));

        var abox = new ABoxManager(_store);
        var aboxGraph = new OntoNamedNode(ks.ABoxGraph);

        // Individuals already in the graph, plus the ones this chunk mints —
        // relation targets resolve against both.
        var individuals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (iri, label) in abox.LabelIndex(ks))
        {
            individuals[Vocabulary.NormLabel(label)] = iri;
        }

        var unknownClasses = new Dictionary<string, int>(StringComparer.Ordinal);
        var provenance = new List<string>();
        var individualsAdded = 0;
        var assertionsAdded = 0;
        var pendingAdded = 0;

        // Pass 1: materialise every mention so relations can point at any
        // individual in the chunk, not just the ones declared before them.
        var resolved = new List<(AboxIndividual Mention, string Iri)>();
        foreach (var mention in delta.Individuals)
        {
            if (!classIndex.TryGetValue(Vocabulary.NormLabel(mention.Class), out var classIri))
            {
                unknownClasses[mention.Class] =
                    unknownClasses.TryGetValue(mention.Class, out var seen) ? seen + 1 : 1;
                continue;
            }

            var key = Vocabulary.NormLabel(mention.Label);
            if (!individuals.TryGetValue(key, out var iri))
            {
                iri = abox.CreateIndividual(ks, mention.Label, classIri);
                _store.AddQuads(aboxGraph, new[]
                {
                    new OntoQuad(new OntoNamedNode(iri), Vocabulary.RdfsLabel, new OntoLiteral(mention.Label), aboxGraph),
                });
                individuals[key] = iri;
                individualsAdded++;
                provenance.Add(FactKey.IndividualKey(iri));
            }
            else
            {
                // Already known: keep the type assertion current, then reuse.
                abox.AddType(ks, iri, classIri);
            }
            resolved.Add((mention, iri));
        }

        // Pass 2: attributes and relations.
        foreach (var (mention, iri) in resolved)
        {
            foreach (var attribute in mention.Attributes)
            {
                if (!dataProperties.TryGetValue(Vocabulary.NormLabel(attribute.Property), out var propertyIri))
                {
                    continue;
                }
                if (abox.AddDataAssertion(ks, iri, propertyIri, attribute.Value, datatype: null))
                {
                    assertionsAdded++;
                    provenance.Add(StatementProvenanceService.AssertionKey(
                        iri, propertyIri, "data", target: null, value: attribute.Value));
                }
            }

            foreach (var relation in mention.Relations)
            {
                if (!objectProperties.TryGetValue(Vocabulary.NormLabel(relation.Property), out var propertyIri) ||
                    !individuals.TryGetValue(Vocabulary.NormLabel(relation.Target), out var targetIri))
                {
                    // The target was never resolved — hand it to the manual queue.
                    pendingAdded++;
                    continue;
                }
                if (abox.AddObjectAssertion(ks, iri, propertyIri, targetIri))
                {
                    assertionsAdded++;
                    provenance.Add(StatementProvenanceService.AssertionKey(
                        iri, propertyIri, "object", target: targetIri, value: null));
                }
            }
        }

        return new ExtractionMergeResult(
            ClassesAdded: 0,
            PropertiesAdded: 0,
            AxiomsAdded: 0,
            individualsAdded,
            assertionsAdded,
            pendingAdded,
            unknownClasses,
            provenance);
    }

    private static Dictionary<string, string> BuildIndex(IEnumerable<(string Label, string Iri)> entries)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, iri) in entries)
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            index.TryAdd(Vocabulary.NormLabel(label), iri);
        }
        return index;
    }

    private static string TermText(object term) => term switch
    {
        OntoNamedNode n => n.Value,
        OntoLiteral l => l.Value,
        _ => term.ToString() ?? string.Empty,
    };
}
