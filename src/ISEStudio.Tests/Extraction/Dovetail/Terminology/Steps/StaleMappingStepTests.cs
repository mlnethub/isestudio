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

public class StaleMappingStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public StaleMappingStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step1",
            BaseIri: "http://goodcrew.local/ks/test/term-step1/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_RemovesStaleMappingsAndSeedsCarry()
    {
        // First sync maps Pump + Motor; then the TBox shrinks to Pump only.
        // The init half of the step builds the carry; the pass half must
        // clear the Motor concept's op:mapsTo triple (stale_mappings_removed
        // == 1) exactly like the Sync_clears_stale_mappings whole-sync test.
        SeedClasses("Pump", "Motor");
        var svc = new TerminologyService(_fx.Store);
        svc.SyncAsync(_ks, CancellationToken.None);

        ReplaceTBox("Pump");

        var step = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance);
        var carry = await step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            CancellationToken.None);

        Assert.Null(carry.Error);
        Assert.Equal(1, carry.StaleMappingsRemoved);
        Assert.Equal($"{_ks.VocabularyGraph}#scheme-extracted", carry.SchemeIri);
        Assert.NotNull(carry.View);
        Assert.NotNull(carry.PreView);
        Assert.Equal(0, carry.TermsAdded);

        var view = new SkosManager(_fx.Store).BuildView(_ks);
        var motor = view.Concepts.Single(c => c.DisplayLabel == "Motor");
        Assert.Null(motor.MappedEntityIri);
    }

    [Fact]
    public async Task ExecuteAsync_NullKs_ReturnsErrorCarry()
    {
        // PrepareCarry dereferences the KsContext — a null one throws
        // inside the step, which must convert it to an Error carry (D5)
        // instead of propagating.
        var svc = new TerminologyService(_fx.Store);
        var step = new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance);

        var carry = await step.ExecuteAsync(
            new TerminologyInput(null!, Guid.NewGuid(), null, false),
            CancellationToken.None);

        Assert.NotNull(carry.Error);
        Assert.True(carry.Skipped);
        Assert.Null(carry.SchemeIri);
    }

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

    private void ReplaceTBox(params string[] labels)
    {
        var existing = _fx.Store.Match(graphIri: _ks.TBoxGraph);
        if (existing.Count > 0)
        {
            _fx.Store.RemoveQuads(new OntoNamedNode(_ks.TBoxGraph), existing);
        }
        SeedClasses(labels);
    }
}
