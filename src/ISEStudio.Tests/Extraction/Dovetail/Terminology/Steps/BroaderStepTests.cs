using ISEStudio.Application.Vocabulary;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Ontology;
using ISEStudio.Tests.Ontology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology.Steps;

public class BroaderStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public BroaderStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step4",
            BaseIri: "http://goodcrew.local/ks/test/term-step4/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_SeedsBroaderFromSubclassRelations()
    {
        // Mirrors Sync_seeds_broader_from_subclass_relations:
        // "Centrifugal Pump" subclasses "Pump" — the broader pass must add
        // a skos:broader triple on the child pointing at the parent concept.
        SeedMutation(
            classes: new[] { "Pump", "Centrifugal Pump" },
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: new[] { new AxiomMutation("subclass", Sub: "Centrifugal Pump", Super: "Pump") });

        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = await new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);
        var synced = await new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance)
            .ExecuteAsync(input, init, CancellationToken.None);
        var aliased = await new AliasStep(svc, NullLogger<AliasStep>.Instance)
            .ExecuteAsync(input, synced, CancellationToken.None);

        var step = new BroaderStep(svc, NullLogger<BroaderStep>.Instance);
        var carry = await step.ExecuteAsync(input, aliased, CancellationToken.None);

        Assert.Null(carry.Carry.Error);
        Assert.Equal(1, carry.Carry.BroaderAdded);
        Assert.Equal(2, carry.Carry.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var child = view.Concepts.Single(c => c.DisplayLabel == "Centrifugal Pump");
        var parent = view.Concepts.Single(c => c.DisplayLabel == "Pump");
        Assert.Contains(parent.Iri, child.Broader);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // Same synthetic-throw pin as EntitySyncStepTests: SchemeIri
        // non-null passes the guard, the null View throws inside the
        // pass, and the step converts it to an Error carry (D5).
        var svc = new TerminologyService(_fx.Store);
        var step = new BroaderStep(svc, NullLogger<BroaderStep>.Instance);
        var malformed = new AliasCarry(new TermSyncCarry("http://x/scheme", null, null, 0));

        var carry = await step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Carry.Error);
        Assert.True(carry.Carry.Skipped);
        Assert.Null(carry.Carry.SchemeIri);
    }

    // SeedMutation helper — identical to StaleMappingStepTests.
    private void SeedMutation(
        IReadOnlyList<string> classes,
        IReadOnlyList<string> objectProperties,
        IReadOnlyList<string> dataProperties,
        IReadOnlyList<AxiomMutation> axioms)
    {
        var mutation = new OntologyMutation(
            Classes: classes.Select(l => new ClassMutation(l)).ToArray(),
            ObjectProperties: objectProperties.Select(l => new PropertyMutation(l, "object")).ToArray(),
            DataProperties: dataProperties.Select(l => new PropertyMutation(l, "data")).ToArray(),
            Axioms: axioms);
        var quads = SchemaBuilder.BuildMutation(_ks.BaseIri, mutation, _ks.TBoxGraph);
        _fx.Store.AddQuads(new OntoNamedNode(_ks.TBoxGraph), quads);
    }
}
