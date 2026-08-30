using Dovetail;
using ISEStudio.Audit;
using ISEStudio.Conflicts;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.ABox;
using ISEStudio.Extraction.Dovetail.ABox.Steps;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.AgentChain.Steps;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using ISEStudio.Extraction.Dovetail.TBox;
using ISEStudio.Extraction.Dovetail.TBox.Steps;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;
using ISEStudio.Infrastructure.Persistence;
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
        services.AddSingleton<AdjudicatorStep>(sp =>
            new AdjudicatorStep(
                verify: sp.GetRequiredService<TBoxVerifyService>(),
                logger: sp.GetRequiredService<ILogger<AdjudicatorStep>>()));
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
        // interface-keyed concrete factory). SCOPED on purpose (final-review
        // MEDIUM fix): the orchestrator resolves AgentChainPipeline from the
        // per-job scope, so the steps, the agents behind them, and their
        // shared DbContext live per job — the P1-4 lifecycle — instead of
        // one root-captured instance for the process. Each factory returns
        // null when the underlying interface is not registered so the
        // registration tests can assert a missing agent surfaces as a null
        // step (the pipeline itself then fails at ExecuteAsync, not at
        // resolution — latent; production always wires the forwarders).
        services.AddScoped<ConflictAgentStep>(sp =>
        {
            var agent = sp.GetService<IConflictAgent>();
            return agent is null
                ? null!
                : new ConflictAgentStep(
                    agent: agent,
                    logger: sp.GetRequiredService<ILogger<ConflictAgentStep>>());
        });

        services.AddScoped<StructureAgentStep>(sp =>
        {
            var agent = sp.GetService<IStructureAgent>();
            return agent is null
                ? null!
                : new StructureAgentStep(
                    agent: agent,
                    logger: sp.GetRequiredService<ILogger<StructureAgentStep>>());
        });

        services.AddScoped<StatsRefreshStep>(sp =>
        {
            var stats = sp.GetService<IKnowledgeStatsService>();
            return stats is null
                ? null!
                : new StatsRefreshStep(
                    stats: stats,
                    logger: sp.GetRequiredService<ILogger<StatsRefreshStep>>());
        });

        // 8. Terminology slice 4 step classes (per spec §6.2 + §5 D9).
        // SCOPED for the same per-job lifecycle reason as §7: the
        // orchestrator resolves TerminologyPipeline from the per-job scope,
        // so the steps live per job. The four pass steps depend only on the
        // singleton TerminologyService (registered plainly); ProposalStep
        // holds the scoped TerminologyAgent + DbContext and reuses the §7
        // null! factory pattern so a missing agent surfaces as a null step.
        services.AddScoped<StaleMappingStep>();
        services.AddScoped<EntitySyncStep>();
        services.AddScoped<AliasStep>();
        services.AddScoped<BroaderStep>();
        services.AddScoped<ProposalStep>(sp =>
        {
            var agent = sp.GetService<TerminologyAgent>();
            return agent is null
                ? null!
                : new ProposalStep(
                    agent: agent,
                    db: sp.GetRequiredService<ISEStudioDbContext>(),
                    options: sp.GetRequiredService<IOptions<ISEStudioOptions>>(),
                    logger: sp.GetRequiredService<ILogger<ProposalStep>>());
        });

        // 9. Job slice 5 step classes + 3 pipelines + router (per spec §6.1).
        //    SCOPED: orchestrator resolves JobPipeline from per-job scope (Slice 3 R2 lifecycle).
        //    NoOpAgentStep / PerPhaseCatchStep<TOut> / ChainAdapter / NoOpSegment3 are static
        //    factories or generic types — created inline inside pipelines, NOT registered.
        services.AddScoped<TBoxLayerStep>();
        services.AddScoped<ABoxLayerStep>();
        services.AddScoped<AgentStep>();
        services.AddScoped<CorpusStep>();
        services.AddScoped<HierarchyStep>();
        services.AddScoped<TerminologyStep>();
        services.AddScoped<TBoxOnlyJobPipeline>();
        services.AddScoped<ABoxOnlyJobPipeline>();
        services.AddScoped<CombinedJobPipeline>();
        services.AddScoped<JobPipelineRouter>();

        // Register 3-arity adapters used by Job pipelines (DOVE008 compliance, Task 4
        // architectural compromise). MS.DI open-generic self-registration enables
        // Dovetail-generated pipeline partial ctors to instantiate these.
        services.AddScoped(typeof(NoOpSegment<,,>));
        services.AddScoped(typeof(ChainAdapter<,,>));

        return services;
    }
}
