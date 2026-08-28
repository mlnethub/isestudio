using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.TBox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

public class ExtractionOrchestratorTBoxPipelineTests
{
    [Fact]
    public void TBoxChunkPipeline_IsResolvable_FromOrchestratorServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<TBoxVerifyService>();
        services.AddSingleton<CorpusRecoveryService>();
        services.AddSingleton<HierarchyRecoveryService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TBoxChunkPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void TBoxChunkPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<TBoxVerifyService>();
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TBoxChunkPipeline>();
        Assert.Null(pipeline);
    }
}
