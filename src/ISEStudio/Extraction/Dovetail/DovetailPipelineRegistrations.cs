using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ISEStudio.Extraction.Dovetail;

public static class DovetailPipelineRegistrations
{
    /// <summary>
    /// Register all Dovetail pipelines + segment classes + adapters.
    /// Idempotent within a single ServiceCollection — calling twice on the
    /// same collection will throw on duplicate registrations; calling twice
    /// on different collections is fine.
    ///
    /// AdjudicatorStep is self-fail-soft (catches own adjudicator exception
    /// and populates DenotationFallback per Task 5b refactor, commit
    /// 8053735) — no external FailSoftSegment wrapping is needed. The
    /// FailSoftSegment&lt;TIn, TOut&gt; class remains in the codebase (Task 4)
    /// as a future-use helper. Corpus / HierarchyRecoveryStep are wrapped
    /// in OptionalSegment — DI resolves the real step if the corresponding
    /// Service is registered, else falls back to a NoOpSegment.
    /// </summary>
    public static IServiceCollection AddDovetailPipelines(this IServiceCollection services)
    {
        // 1. Dovetail pipeline partial classes (TBoxChunkPipeline, TBoxJobPipeline).
        services.AddPipelines();

        // 2. Concrete IRunWithExtractionGuard for GuardedSegment (Task 9).
        services.AddSingleton<IRunWithExtractionGuard, ExtractionGuard>();

        // 3. Chunk-level step classes — all public sealed with TBoxVerifyService.
        services.AddSingleton<CriticStep>();
        services.AddSingleton<AdjudicatorStep>();
        services.AddSingleton<DenotationStep>();
        services.AddSingleton<ChunkMergeStep>();

        // 4. Job-level step classes — CorpusRecoveryService / HierarchyRecoveryService
        // are nullable on the constructors, so DI registers them via the
        // service-provider's GetService<T>() inside a factory. This allows
        // OptionalSegment to fall back to NoOpSegment when the service is
        // missing.
        services.AddSingleton<ChunkPipelineStep>();
        services.AddSingleton<JobMergeStep>();
        services.AddSingleton<CorpusRecoveryStep>(sp =>
            new CorpusRecoveryStep(sp.GetService<CorpusRecoveryService>()));
        services.AddSingleton<HierarchyRecoveryStep>(sp =>
            new HierarchyRecoveryStep(sp.GetService<HierarchyRecoveryService>()));

        // 5. OptionalSegment wrapping for Corpus/Hierarchy Recovery is
        // applied inside CorpusRecoveryStep.ExecuteAsync /
        // HierarchyRecoveryStep.ExecuteAsync themselves: each step takes a
        // nullable Service and returns its Enabled:false wrapper when the
        // service is absent. We do NOT register
        // IPipelineSegment<TBoxJobInput, *> here because TBoxJobPipeline's
        // [Segment] ctor parameter types are the concrete
        // CorpusRecoveryStep / HierarchyRecoveryStep classes — those
        // factory registrations are unreachable at runtime. The concrete
        // AddSingleton<CorpusRecoveryStep>(...) /
        // AddSingleton<HierarchyRecoveryStep>(...) on lines 48-51 are what
        // the pipeline actually resolves. (See Slice 1 final review F-1.)

        return services;
    }
}
