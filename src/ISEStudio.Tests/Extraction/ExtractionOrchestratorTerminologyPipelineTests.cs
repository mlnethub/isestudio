using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Llm;
using ISEStudio.Tests.Extraction;
using ISEStudio.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// DI-level tests for the Dovetail terminology pipeline resolution through
/// the same registration surface the orchestrator uses
/// (<see cref="DovetailPipelineRegistrations.AddDovetailPipelines"/>).
/// </summary>
public class ExtractionOrchestratorTerminologyPipelineTests
{
    [Fact]
    public void TerminologyPipeline_IsResolvable_FromOrchestratorServices()
    {
        using var contexts = new SqliteContextFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton(new TerminologyService(null));
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(contexts);
        services.AddScoped<ISEStudioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<ISEStudioDbContext>>().CreateDbContext());
        services.AddSingleton<IChatClientFactory>(FakeChatClientFactory.Default);
        services.AddScoped<TerminologyAgent>();
        services.AddDovetailPipelines();
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void TerminologyPipeline_ResolveFails_WhenAddDovetailPipelinesOmitted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        // Intentionally NOT calling AddDovetailPipelines().
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.Null(pipeline);
    }
}
