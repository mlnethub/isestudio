using Dovetail;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail;

public class DovetailPipelineRegistrationsTests
{
    [Fact]
    public void AddDovetailPipelines_RegistersBothPipelines()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var chunk = sp.GetService<TBoxChunkPipeline>();
        var job = sp.GetService<TBoxJobPipeline>();

        Assert.NotNull(chunk);
        Assert.NotNull(job);
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAllChunkStepClasses()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<CriticStep>());
        Assert.NotNull(sp.GetService<AdjudicatorStep>());
        Assert.NotNull(sp.GetService<DenotationStep>());
        Assert.NotNull(sp.GetService<ChunkMergeStep>());
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAllJobStepClasses()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<ChunkPipelineStep>());
        Assert.NotNull(sp.GetService<CorpusRecoveryStep>());
        Assert.NotNull(sp.GetService<HierarchyRecoveryStep>());
        Assert.NotNull(sp.GetService<JobMergeStep>());
    }

    [Fact]
    public void AddDovetailPipelines_RegistersAdjudicatorStepDirectly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ISEStudioOptions { AutoApplyFloor = 0.85 }));
        services.AddSingleton<TBoxVerifyService>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        // Pipeline ctor parameter type is AdjudicatorStep directly (no
        // external FailSoftSegment wrapper). AdjudicatorStep is self-fail-soft.
        var adjudicator = sp.GetService<AdjudicatorStep>();
        Assert.NotNull(adjudicator);
    }

    [Fact]
    public void AddDovetailPipelines_RegistersIRunWithExtractionGuard()
    {
        var services = new ServiceCollection();
        // ExtractionGuard's ctor depends on ExtractionJobStore, so the
        // store must be resolvable for GetService<IRunWithExtractionGuard>()
        // to succeed. Production wires AddExtractionServices which registers
        // it; this unit test exercises AddDovetailPipelines alone and so
        // installs a minimal stub.
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(new SqliteContextFactory());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ExtractionJobStore>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var guard = sp.GetService<IRunWithExtractionGuard>();
        Assert.NotNull(guard);
    }
}
