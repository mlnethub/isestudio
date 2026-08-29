using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Terminology;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Terminology;

public class TerminologyPipelineTests
{
    [Fact]
    public void TerminologyPipeline_DovetailEmitsExecuteAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // StoreWrapper is nullable on TerminologyService's ctor, so a null
        // store is enough to make the 4 pass steps resolvable; the
        // ProposalStep factory yields null! (no agent registered) and the
        // pipeline still constructs (latent — production always wires it).
        services.AddSingleton(new TerminologyService(null));
        services.AddDovetailPipelines();
        // Task 6's §8 factory (DovetailPipelineRegistrations) yields a null!
        // ProposalStep when no TerminologyAgent is registered; until then the
        // generated AddPipelines registration is a plain transient whose ctor
        // deps (agent / ISEStudioDbContext / IOptions) are absent here, so
        // the test stands in with the same null! factory (Slice 3 §7
        // pattern). The emit under test is the generated ExecuteAsync.
        services.AddSingleton<ProposalStep>(_ => null!);
        using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetService<TerminologyPipeline>();
        Assert.NotNull(pipeline);
    }
}
