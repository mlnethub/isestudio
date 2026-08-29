using Dovetail;
using ISEStudio.Audit;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Knowledge;
using ISEStudio.Ontology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // 6. ABox-level step classes (Slice 2). All nullable service
        // dependencies — DI registers them with whatever services are
        // available; missing services yield steps with null service refs
        // (fail-soft path; see spec §4 D4).
        services.AddSingleton<CandidateGatherStep>(sp =>
            new CandidateGatherStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<EmbeddingMatchStep>(sp =>
            new EmbeddingMatchStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<LLMJudgeStep>(sp =>
            new LLMJudgeStep(sp.GetService<DuplicateJudge>()));
        services.AddSingleton<MergeApplyStep>(sp =>
            new MergeApplyStep(sp.GetService<OntologyEditor>(), sp.GetService<AuditLogService>()));
        services.AddSingleton<CascadeRetypeStep>(sp =>
            new CascadeRetypeStep(sp.GetService<OntologyEditor>(), sp.GetService<AuditLogService>()));
        services.AddSingleton<FinalMergeStep>();

        // 7. AgentChain slice 3 step classes (per spec §6.2 + §5 D6 —
        // interface-keyed concrete factory). Each step takes a nullable
        // interface dep, and the factory returns null when the underlying
        // agent / stats service is not registered so the pipeline can be
        // null-tested and `AgentChainPipeline` fails fast (its [Segment]
        // ctor params are non-nullable). When all deps are registered
        // (production runtime), the steps and pipeline resolve normally.
        services.AddSingleton<ConflictAgentStep>(sp =>
        {
            var agent = sp.GetService<IConflictAgent>();
            return agent is null
                ? null!
                : new ConflictAgentStep(
                    agent: agent,
                    logger: sp.GetRequiredService<ILogger<ConflictAgentStep>>());
        });

        services.AddSingleton<StructureAgentStep>(sp =>
        {
            var agent = sp.GetService<IStructureAgent>();
            return agent is null
                ? null!
                : new StructureAgentStep(
                    agent: agent,
                    logger: sp.GetRequiredService<ILogger<StructureAgentStep>>());
        });

        services.AddSingleton<StatsRefreshStep>(sp =>
        {
            var stats = sp.GetService<IKnowledgeStatsService>();
            return stats is null
                ? null!
                : new StatsRefreshStep(
                    stats: stats,
                    logger: sp.GetRequiredService<ILogger<StatsRefreshStep>>());
        });

        return services;
    }
}
