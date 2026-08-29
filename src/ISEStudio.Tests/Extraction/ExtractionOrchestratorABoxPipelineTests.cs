using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// DI resolvability + ctor-tail seam coverage for the Slice 2 wire of
/// <see cref="ABoxJobPipeline"/> into <see cref="ExtractionOrchestrator"/>.
/// Mirrors <see cref="ExtractionOrchestratorTBoxPipelineTests"/> — the new
/// fields (<c>_aboxPipeline</c>, <c>_duplicateJudge</c>) are nullable and
/// registered only when the Dovetail pipeline extension is called; without
/// <see cref="DovetailPipelineRegistrations.AddDovetailPipelines"/> the
/// orchestrator must still construct (the legacy fallback path uses
/// <c>DuplicateJudge</c> directly).
/// </summary>
public class ExtractionOrchestratorABoxPipelineTests
{
    [Fact]
    public void ABoxJobPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddSingleton<EmbeddingGeneratorFactory>();
        services.AddSingleton<DuplicateJudge>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void ABoxJobPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.Null(pipeline);
    }
}