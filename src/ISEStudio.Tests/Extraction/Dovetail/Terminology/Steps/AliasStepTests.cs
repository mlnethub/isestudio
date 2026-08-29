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

public class AliasStepTests : IClassFixture<TerminologyServiceFixture>, IAsyncLifetime
{
    private readonly TerminologyServiceFixture _fx;
    private readonly KsContext _ks;

    public AliasStepTests(TerminologyServiceFixture fx)
    {
        _fx = fx;
        _ks = new KsContext(
            GraphIri: "http://goodcrew.local/ks/test/term-step3",
            BaseIri: "http://goodcrew.local/ks/test/term-step3/onto#",
            Name: "Step tests");
    }

    public Task InitializeAsync()
    {
        _fx.Store.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_AttachesEntityLabelAsAlias()
    {
        // Mirrors Sync_adds_entity_label_as_alias_when_pref_label_differs:
        // a manually-curated concept is mapped to Pump but its pref label
        // is "Fluid Mover" — the alias pass must attach "Pump" as an
        // skos:altLabel without touching the curated pref label.
        SeedClasses("Pump");
        var manager = new SkosManager(_fx.Store);
        SeedDefaultScheme(manager);
        var pumpIri = $"{_ks.BaseIri}Pump";
        manager.CreateConcept(_ks,
            $"{_ks.VocabularyGraph}#scheme-extracted",
            new SkosConceptData(
                Iri: $"{_ks.VocabularyGraph}#concept-FluidMover",
                PrefLabel: "Fluid Mover",
                Language: "en",
                MappedEntityIri: pumpIri));

        var svc = new TerminologyService(_fx.Store);
        var input = new TerminologyInput(_ks, Guid.NewGuid(), null, false);
        var init = await new StaleMappingStep(svc, NullLogger<StaleMappingStep>.Instance)
            .ExecuteAsync(input, CancellationToken.None);
        var synced = await new EntitySyncStep(svc, NullLogger<EntitySyncStep>.Instance)
            .ExecuteAsync(input, init, CancellationToken.None);

        var step = new AliasStep(svc, NullLogger<AliasStep>.Instance);
        var carry = await step.ExecuteAsync(input, synced, CancellationToken.None);

        Assert.Null(carry.Carry.Error);
        Assert.Equal(1, carry.Carry.AliasesAdded);
        Assert.Equal(0, carry.Carry.TermsAdded);

        var view = manager.BuildView(_ks);
        var concept = view.Concepts.Single(c => c.MappedEntityIri == pumpIri);
        Assert.Equal("Fluid Mover", concept.DisplayLabel);
        var alias = Assert.Single(concept.AltLabels);
        Assert.Equal("Pump", alias.Value);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedCarry_ReturnsErrorCarry()
    {
        // Same synthetic-throw pin as EntitySyncStepTests: SchemeIri
        // non-null passes the guard, the null View throws inside the
        // pass, and the step converts it to an Error carry (D5).
        var svc = new TerminologyService(_fx.Store);
        var step = new AliasStep(svc, NullLogger<AliasStep>.Instance);
        var malformed = new EntitySyncCarry(new TermSyncCarry("http://x/scheme", null, null, 0));

        var carry = await step.ExecuteAsync(
            new TerminologyInput(_ks, Guid.NewGuid(), null, false),
            malformed,
            CancellationToken.None);

        Assert.NotNull(carry.Carry.Error);
        Assert.True(carry.Carry.Skipped);
        Assert.Null(carry.Carry.SchemeIri);
    }

    private void SeedDefaultScheme(SkosManager manager) =>
        manager.CreateScheme(_ks, new SkosSchemeData(
            Iri: $"{_ks.VocabularyGraph}#scheme-extracted",
            Title: "Step tests terminology",
            DefaultLanguage: "en",
            Origin: "extraction"));

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
