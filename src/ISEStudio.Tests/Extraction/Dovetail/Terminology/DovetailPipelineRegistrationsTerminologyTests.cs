using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class DovetailPipelineRegistrationsTerminologyTests
{
    private static void AddDbContexts(IServiceCollection services, SqliteContextFactory contexts)
    {
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
    }

    [Fact]
    public void PassSteps_AreResolvable_WhenTerminologyServiceRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new TerminologyService(null));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<StaleMappingStep>());
        Assert.NotNull(sp.GetService<EntitySyncStep>());
        Assert.NotNull(sp.GetService<AliasStep>());
        Assert.NotNull(sp.GetService<BroaderStep>());
    }

    [Fact]
    public void ProposalStep_ResolvesNull_WhenTerminologyAgentMissing()
    {
        // The §8 factory returns null! when the agent is absent (Slice 3
        // null! 口径) — the registration tests pin that shape.
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        AddDbContexts(services, contexts);
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<ProposalStep>());
    }

    [Fact]
    public void TerminologyPipeline_IsResolvable_WhenAllStepsResolve()
    {
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        AddDbContexts(services, contexts);
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddScoped<TerminologyAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<TerminologyPipeline>());
    }

    [Fact]
    public void TerminologyPipeline_ResolveFails_WhenTerminologyServiceMissing()
    {
        // AddDovetailPipelines registers the pipeline (generator) + the §8
        // scoped steps, so MS.DI cannot return null here — a registered
        // type whose ctor deps are missing THROWS InvalidOperationException
        // from GetService (null is only for unregistered types). The §8
        // scoped StaleMappingStep cannot activate without the singleton
        // TerminologyService; the pipeline therefore cannot resolve, and
        // the negative assertion is the exception, not a null.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetService<TerminologyPipeline>());
    }
}
