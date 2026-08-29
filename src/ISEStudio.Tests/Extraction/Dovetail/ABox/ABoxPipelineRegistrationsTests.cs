using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.ABox;

public class ABoxPipelineRegistrationsTests
{
    [Fact]
    public void AddDovetailPipelines_RegistersABoxJobPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<ABoxJobPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAllABoxStepClasses()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<CandidateGatherStep>());
        Assert.NotNull(sp.GetService<EmbeddingMatchStep>());
        Assert.NotNull(sp.GetService<LLMJudgeStep>());
        Assert.NotNull(sp.GetService<MergeApplyStep>());
        Assert.NotNull(sp.GetService<CascadeRetypeStep>());
        Assert.NotNull(sp.GetService<FinalMergeStep>());
    }

    [Fact]
    public void AddDovetailPipelines_RegistersABoxSteps_WithNullableServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions { DuplicateAutoApplyFloor = 0.90 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        // Steps accept nullable service deps; if DuplicateJudge / OntologyEditor
        // are not registered, the step is still resolvable with null services.
        var gather = sp.GetRequiredService<CandidateGatherStep>();
        var merge = sp.GetRequiredService<MergeApplyStep>();
        Assert.NotNull(gather);
        Assert.NotNull(merge);
    }

    [Fact]
    public void DuplicateAutoApplyFloor_Default_Is090()
    {
        var options = new ISEStudioOptions();
        Assert.Equal(0.90, options.DuplicateAutoApplyFloor);
    }

    [Fact]
    public void DuplicateAutoApplyFloor_CanBeOverridden()
    {
        var options = new ISEStudioOptions { DuplicateAutoApplyFloor = 0.95 };
        Assert.Equal(0.95, options.DuplicateAutoApplyFloor);
    }
}
