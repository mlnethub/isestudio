using OnToPilot.Application.Foundation;
using OnToPilot.Ontology;
using Xunit;

namespace OnToPilot.Tests.Ontology;

public sealed class OntologyViewBuilderTests
{
    [Fact]
    public async Task BuildFromStoreAsync_with_null_store_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store: null, graphIri: "http://x/graph", CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Empty(view.ObjectProperties);
        Assert.Empty(view.DataProperties);
        Assert.Empty(view.Axioms.SubclassOf);
        Assert.Empty(view.Axioms.DisjointWith);
        Assert.Empty(view.Axioms.EquivalentClass);
        Assert.Empty(view.Labels);
        Assert.Equal(0, view.Stats.ClassCount);
        Assert.Equal(0, view.Stats.PropertyCount);
        Assert.Equal(0, view.Stats.AxiomCount);
        Assert.Null(view.KnowledgeSystem);
    }

    [Fact]
    public async Task BuildFromNQuadsAsync_with_empty_bytes_returns_empty_envelope()
    {
        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromNQuadsAsync(
            tboxShard: Array.Empty<byte>(), CancellationToken.None);

        Assert.NotNull(view);
        Assert.Empty(view.Classes);
        Assert.Equal(0, view.Stats.ClassCount);
    }

    [Fact]
    public async Task BuildFromStoreAsync_extracts_single_class_with_label_and_comment()
    {
        using var dir = new TempDir();
        using var store = new StoreWrapper(dir.Path);
        store.LoadTurtle(
            """
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            <urn:Animal> a owl:Class ; rdfs:label "Animal" ; rdfs:comment "A living thing." .
            """u8.ToArray(),
            new Oxigraph.NamedNode("http://example.com/graph"));

        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store, "http://example.com/graph", CancellationToken.None);

        Assert.Single(view.Classes);
        var c = view.Classes[0];
        Assert.Equal("urn:Animal", c.Iri);
        Assert.Equal("Animal", c.Label);
        Assert.Equal("Animal", c.Local);
        Assert.Equal("A living thing.", c.Comment);
        Assert.Empty(c.Superclasses);
    }

    [Fact]
    public async Task BuildFromStoreAsync_extracts_superclasses_via_subClassOf()
    {
        using var dir = new TempDir();
        using var store = new StoreWrapper(dir.Path);
        store.LoadTurtle(
            """
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            <urn:Animal> a owl:Class ; rdfs:label "Animal" .
            <urn:Dog> a owl:Class ; rdfs:label "Dog" ; rdfs:subClassOf <urn:Animal> .
            """u8.ToArray(),
            new Oxigraph.NamedNode("http://example.com/graph"));

        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store, "http://example.com/graph", CancellationToken.None);

        Assert.Equal(2, view.Classes.Count);
        var dog = view.Classes.Single(c => c.Local == "Dog");
        Assert.Equal(new[] { "urn:Animal" }, dog.Superclasses);
    }

    [Fact]
    public async Task BuildFromStoreAsync_splits_object_vs_data_properties()
    {
        using var dir = new TempDir();
        using var store = new StoreWrapper(dir.Path);
        store.LoadTurtle(
            """
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
            <urn:Pet> a owl:Class ; rdfs:label "Pet" .
            <urn:hasOwner> a owl:ObjectProperty ; rdfs:label "has owner" ;
                           rdfs:domain <urn:Pet> ; rdfs:range <urn:Pet> .
            <urn:age> a owl:DatatypeProperty ; rdfs:label "age" ;
                       rdfs:domain <urn:Pet> ; rdfs:range xsd:integer .
            """u8.ToArray(),
            new Oxigraph.NamedNode("http://example.com/graph"));

        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store, "http://example.com/graph", CancellationToken.None);

        Assert.Single(view.ObjectProperties);
        Assert.Single(view.DataProperties);

        var obj = view.ObjectProperties[0];
        Assert.Equal("hasOwner", obj.Local);
        Assert.Equal("urn:Pet", obj.Domain);
        Assert.Equal("Pet", obj.DomainLabel);
        Assert.Equal("urn:Pet", obj.Range);
        Assert.Equal("Pet", obj.RangeLabel);

        var dat = view.DataProperties[0];
        Assert.Equal("age", dat.Local);
        Assert.Equal("urn:Pet", dat.Domain);
        // Oxigraph resolves the `xsd:integer` CURIE to the full IRI; the verbatim
        // Prop(...) stores that resolved value as-is (mirrors Python schema.py:346).
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", dat.Range);
        Assert.Null(dat.RangeLabel);
        Assert.Equal(2, view.Stats.PropertyCount);
    }

    [Fact]
    public async Task BuildFromStoreAsync_extracts_disjointWith_and_equivalentClass_axioms()
    {
        using var dir = new TempDir();
        using var store = new StoreWrapper(dir.Path);
        store.LoadTurtle(
            """
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            <urn:Cat> a owl:Class .
            <urn:Dog> a owl:Class .
            <urn:Mammal> a owl:Class .
            <urn:Cat> owl:disjointWith <urn:Dog> .
            <urn:Mammal> owl:equivalentClass <urn:Cat> .
            """u8.ToArray(),
            new Oxigraph.NamedNode("http://example.com/graph"));

        var builder = new OntologyViewBuilder();
        var view = await builder.BuildFromStoreAsync(
            store, "http://example.com/graph", CancellationToken.None);

        Assert.Single(view.Axioms.DisjointWith);
        Assert.Equal("urn:Cat", view.Axioms.DisjointWith[0].A);
        Assert.Equal("urn:Dog", view.Axioms.DisjointWith[0].B);

        Assert.Single(view.Axioms.EquivalentClass);
        Assert.Equal("urn:Mammal", view.Axioms.EquivalentClass[0].A);
        Assert.Equal("urn:Cat", view.Axioms.EquivalentClass[0].B);

        Assert.Equal(2, view.Stats.AxiomCount);  // 0 subClassOf + 1 disjoint + 1 equiv
    }

    [Fact]
    public async Task BuildFromNQuadsAsync_matches_BuildFromStoreAsync_for_same_graph()
    {
        using var dir = new TempDir();
        using var store = new StoreWrapper(dir.Path);
        const string graphIri = "http://example.com/graph";
        store.LoadTurtle(
            """
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            <urn:Animal> a owl:Class ; rdfs:label "Animal" .
            <urn:Dog> a owl:Class ; rdfs:label "Dog" ; rdfs:subClassOf <urn:Animal> .
            """u8.ToArray(),
            new Oxigraph.NamedNode(graphIri));

        var shard = store.DumpNQuads(new Oxigraph.NamedNode(graphIri));

        var builder = new OntologyViewBuilder();
        var fromStore = await builder.BuildFromStoreAsync(store, graphIri, CancellationToken.None);
        var fromShard = await builder.BuildFromNQuadsAsync(shard, CancellationToken.None);

        Assert.Equal(fromStore.Classes.Count, fromShard.Classes.Count);
        Assert.Equal(fromStore.Stats, fromShard.Stats);
        Assert.Equal(
            fromStore.Axioms.SubclassOf.Select(a => (a.Sub, a.Super)),
            fromShard.Axioms.SubclassOf.Select(a => (a.Sub, a.Super)));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ontopilot-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}