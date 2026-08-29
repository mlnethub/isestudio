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

public class EntitySyncStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public EntitySyncStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step2",
            BaseIri: "http://goodcrew.local/ks/test/term-step2/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_CreatesMappedConceptsAndCounts()
    {
        SeedClasses("Pump", "Motor");
        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = await new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);

        var step = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance);
        var carry = await step.ExecuteAsync(input, init, CancellationToken.None);

        Assert.Null(carry.Carry.Error);
        Assert.Equal(2, carry.Carry.TermsAdded);
        Assert.Equal(2, carry.Carry.TermsMapped);
        Assert.Equal(0, carry.Carry.MappingConflicts);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        Assert.Equal(2, view.Stats.ConceptCount);
        Assert.Equal(2, view.Stats.MappedCount);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // SchemeIri non-null passes the guard; the null View then throws
        // inside the pass — the step must convert that to an Error carry
        // (D5) instead of propagating. (Inducing a real store exception is
        // nondeterministic on Windows — Oxigraph handle behavior — so the
        // catch contract is pinned with a synthetic throw.)
        var svc = new TerminologyService(_fx.Store);
        var step = new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance);
        var malformed = new TermSyncCarry("http://x/scheme", null, null, 0);

        var carry = await step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Carry.Error);
        Assert.True(carry.Carry.Skipped);
        Assert.Null(carry.Carry.SchemeIri);
    }

    // SeedClasses / SeedMutation helpers — identical to StaleMappingStepTests.
    private void SeedClasses(params string[] labels) =>
        SeedMutation(
            classes: labels,
            objectProperties: Array.Empty<string>(),
            dataProperties: Array.Empty<string>(),
            axioms: Array.Empty<AxiomMutation>());

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
