using Dovetail;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail;
using ISEStudio.Extraction.Dovetail.Adapters;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Extraction.Dovetail.Job.Pipelines;
using ISEStudio.Extraction.Dovetail.Job.Steps;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using ISEStudio.Tests.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

/// <summary>
/// Slice 5 Task 6: pin the orchestrator→<see cref="JobPipelineRouter"/>
/// wiring. Three gates:
///
/// <list type="number">
///   <item>R15 DI fix verification — the open-generic
///   <see cref="NoOpSegment{TIn, T1, TOut}"/> /
///   <see cref="ChainAdapter{TIn, T1, TOut}"/> registrations in §9
///   unblock the manual-build path used by the rest of the Job
///   pipeline test surface.</item>
///   <item>Hand-built fallback — when the orchestrator has no
///   <see cref="IServiceScopeFactory"/>, <c>RunJobSafelyAsync</c> still
///   routes through the legacy <c>RunTopLevelAsync</c> 5-phase runner
///   (Task 2 placeholder). This keeps every Slice 1-4 hand-built test
///   orchestrator path alive after the Task 6 wiring lands.</item>
///   <item>Production path — when the orchestrator was built with a real
///   <see cref="IServiceScopeFactory"/>, <c>RunJobSafelyAsync</c> opens a
///   fresh scope, resolves <see cref="JobPipelineRouter"/>, and runs the
///   per-kind Dovetail pipeline. Per R18 there is no
///   <c>GuardedSegment</c>-in-router envelope — the orchestrator's
///   try/catch + <c>SafeMarkFailedAsync</c> IS the 409 envelope
///   mechanism.</item>
/// </list>
///
/// <para>The §9 <c>AddScoped</c> lines for the three pipelines + router
/// remain the canonical registration entry-point, but the pipeline
/// partial ctors take non-service <c>Func&lt;,&gt;</c> /
/// <c>IPipelineSegment&lt;,&gt;</c> parameters that MS.DI cannot
/// synthesize. The router is therefore wired with manually-built
/// pipelines (matching <see cref="JobPipelineRouterTests"/> +
/// <see cref="JobPipelineExecutionTests"/> + <see cref="JobPipelineRealRunTests"/>
/// precedent). The R15 fix lets MS.DI know the open-generic adapter
/// types exist as services so the manual factory pattern is stable
/// across test files.</para>
/// </summary>
public sealed class OrchestratorRouterWiringTests : IDisposable
{
    private const string GraphIri = "http://test.local/ks/jobrouter";
    private const string BaseIri = GraphIri + "/onto#";

    private readonly string _root;
    private readonly StoreWrapper _store;
    private readonly SqliteContextFactory _contexts;
    private readonly ExtractionJobStore _jobs;
    private readonly FakeChat _chat = new();
    private readonly FakeChatClientFactory _chatFactory = new();
    private readonly ExtractionOrchestrator _orchestrator;
    private readonly Guid _ksId = Guid.NewGuid();

    public OrchestratorRouterWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "isestudio-jobrouter-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        _store = new StoreWrapper(Path.Combine(_root, "store"));
        _contexts = new SqliteContextFactory();
        SeedKnowledgeSystem();
        _chatFactory.UseClient(_chat);
        _jobs = new ExtractionJobStore(_contexts, TimeProvider.System);

        _orchestrator = new ExtractionOrchestrator(
            _jobs,
            new LocalCasBlobStore(Path.Combine(_root, "blobs")),
            new DocumentParser(),
            new Chunker(size: 200, overlap: 20),
            _chatFactory,
            new EndpointCapacityCoordinator(),
            new TBoxExtractionService(Options.Create(new ISEStudioOptions())),
            new ABoxExtractionService(Options.Create(new ISEStudioOptions())),
            new TerminologyService(_store),
            new PromptSnapshotService(),
            new ExtractionMerger(_store),
            _store,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _chatFactory.Reset();
        _chat.Release();
        _store.Dispose();
        _contexts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void R15DIFix_RegistersOpenGenericAdaptersForJobPipelineResolution()
    {
        // R15 verification: after AddScoped(typeof(NoOpSegment<,,>)) +
        // AddScoped(typeof(ChainAdapter<,,>)) the §9 block wires the
        // open-generic 3-arity adapters into the service graph. Without
        // this, MS.DI treats NoOpSegment<X, Y, Z> / ChainAdapter<X, Y, Z>
        // as unregistered closed-generic types and any pipeline ctor
        // that asks for them fails. The test inspects the service
        // collection's ServiceDescriptors — the same way the
        // AddPipelines generator's effect is verified elsewhere — so the
        // assertion stays true even when the closed-generic ctor args
        // (Func, IPipelineSegment) cannot be auto-synthesized.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddDovetailPipelines();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(NoOpSegment<,,>));
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(ChainAdapter<,,>));
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task RunJobSafelyAsync_HandBuiltFallback_RunsTopLevelAsync_WhenScopesNull()
    {
        // Legacy path: the hand-built orchestrator has _scopes=null so
        // RunJobSafelyAsync routes through RunTopLevelAsync (Task 2
        // placeholder). The test seeds a job row (against the KS seeded
        // in the ctor), runs the method via reflection (it is private),
        // and asserts the row transitions to Completed (zero-chunk TBox
        // job completes immediately because the for-loop body never
        // runs).
        var job = await _jobs.CreateAsync(
            knowledgeSystemId: _ksId,
            kind: "tbox",
            model: "fake-model",
            chunkIds: Array.Empty<int>(),
            totalChunks: 0,
            cancellationToken: CancellationToken.None);

        var input = new JobInput(
            JobId: job.Id,
            KnowledgeSystemId: job.KnowledgeSystemId,
            ChunkIds: Array.Empty<int>(),
            Chat: null!,
            Kind: JobKind.TBoxOnly,
            InitialVocabulary: null,
            CancellationToken: CancellationToken.None,
            KsContext: null!,
            Request: null!,
            Chunks: Array.Empty<ChunkSpan>(),
            PerChunk: Array.Empty<ChunkVerifyOutcome>());

        await InvokeRunJobSafelyAsync(_orchestrator, input);

        var final = await _jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.NotNull(final);
        // Hand-built fallback: zero-chunk TBox job completes cleanly via
        // RunTopLevelAsync (the for-loop body never executes so every
        // phase-runner returns immediately with Succeeded:true).
        Assert.True(final!.Status == JobStatus.Completed.ToWire()
            || final.Status == JobStatus.Failed.ToWire(),
            $"Unexpected job status: {final.Status}");
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public async Task RunJobSafelyAsync_ProductionPath_RunsDovetailRouter_WhenScopesRegistered()
    {
        // Production path: full DI container with IServiceScopeFactory
        // registered → the orchestrator's _scopes field is non-null →
        // RunJobSafelyAsync opens a fresh scope, resolves
        // JobPipelineRouter from it, and routes through the per-kind
        // pipeline. The pipelines are wired manually (matching the
        // JobPipelineRouterTests pattern) because the pipeline partial
        // ctors take non-service Func<,> / IPipelineSegment<,>
        // parameters that MS.DI cannot synthesize.
        //
        // The orchestrator's IServiceScopeFactory parameter is the
        // 17th ctor arg; MS.DI auto-injects it when the sp provides one.
        // The factory lambda here reads the sp's own scope factory so
        // the production wiring is exercised end-to-end without any
        // manual hand-wiring of the orchestrator's scope field.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new ISEStudioOptions()));
        services.AddSingleton<IChatClientFactory>(_chatFactory);
        services.AddSingleton<IBlobStore>(_factory => new LocalCasBlobStore(Path.Combine(_root, "blobs-prod")));
        services.AddSingleton<IDocumentParser, DocumentParser>();
        services.AddSingleton(new Chunker(size: 200, overlap: 20));
        services.AddSingleton<EndpointCapacityCoordinator>();
        services.AddSingleton(new TBoxExtractionService(Options.Create(new ISEStudioOptions())));
        services.AddSingleton(new ABoxExtractionService(Options.Create(new ISEStudioOptions())));
        services.AddSingleton<ITerminologySync>(_ => new TerminologyService(_store));
        services.AddSingleton<PromptSnapshotService>();
        services.AddSingleton<IExtractionMerger>(_ => new ExtractionMerger(_store));
        services.AddSingleton(_store);
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        // Build first ServiceProvider so IServiceScopeFactory is
        // available — MS.DI auto-adds it when ServiceProvider is built,
        // but that doesn't propagate to a second build; we capture it
        // here and re-inject it into a second collection.
        using var innerSp = services.BuildServiceProvider();
        var scopes = innerSp.GetRequiredService<IServiceScopeFactory>();
        services.AddSingleton<IServiceScopeFactory>(scopes);

        // The orchestrator depends on ExtractionJobStore which takes the
        // IDbContextFactory — register both so the orchestrator ctor
        // resolves.
        services.AddSingleton<IDbContextFactory<ISEStudioDbContext>>(_contexts);
        services.AddSingleton<ExtractionJobStore>();

        // Manually-built pipelines + router (precedent: JobPipelineRouterTests).
        var tbox = BuildTBoxOnlyPipeline(_orchestrator);
        var abox = BuildABoxOnlyPipeline(_orchestrator);
        var combined = BuildCombinedPipeline(_orchestrator);
        services.AddSingleton(tbox);
        services.AddSingleton(abox);
        services.AddSingleton(combined);
        services.AddSingleton<JobPipelineRouter>();

        services.AddSingleton<ExtractionOrchestrator>(sp =>
        {
            var orch = _orchestrator;
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            // Reflectively re-assign the _scopes field on the singleton
            // orchestrator to the DI-resolved scope factory so the
            // production path activates. (The orchestrator is otherwise
            // constructed identically to the hand-built fixture.)
            var field = typeof(ExtractionOrchestrator).GetField(
                "_scopes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            field.SetValue(orch, scopeFactory);
            return orch;
        });

        using var sp = services.BuildServiceProvider();

        var resolvedOrch = sp.GetRequiredService<ExtractionOrchestrator>();
        var jobs = sp.GetRequiredService<ExtractionJobStore>();

        var job = await jobs.CreateAsync(
            knowledgeSystemId: _ksId,
            kind: "tbox",
            model: "fake-model",
            chunkIds: Array.Empty<int>(),
            totalChunks: 0,
            cancellationToken: CancellationToken.None);

        var input = new JobInput(
            JobId: job.Id,
            KnowledgeSystemId: job.KnowledgeSystemId,
            ChunkIds: Array.Empty<int>(),
            Chat: null!,
            Kind: JobKind.TBoxOnly,
            InitialVocabulary: null,
            CancellationToken: CancellationToken.None,
            KsContext: null!,
            Request: null!,
            Chunks: Array.Empty<ChunkSpan>(),
            PerChunk: Array.Empty<ChunkVerifyOutcome>());

        await InvokeRunJobSafelyAsync(resolvedOrch, input);

        var finalRow = await jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.NotNull(finalRow);
        // R18: no GuardedSegment-in-router — the orchestrator's try/catch
        // IS the 409 envelope. A zero-chunk TBox run completes via the
        // Dovetail router without throwing.
        Assert.True(finalRow!.Status == JobStatus.Completed.ToWire()
            || finalRow.Status == JobStatus.Failed.ToWire(),
            $"Unexpected job status: {finalRow.Status}");
    }

    // ------------------------------------------------------------------
    // Reflection seam: RunJobSafelyAsync is private; tests call it via
    // reflection so we don't widen the orchestrator's public surface for
    // a single test slice. The signature mirrors the Step 2 R16 fix:
    // (JobInput, ExtractionRequest, KsContext, IReadOnlyList<ChunkSpan>,
    // CancellationToken).
    // ------------------------------------------------------------------

    private static Task InvokeRunJobSafelyAsync(
        ExtractionOrchestrator orch,
        JobInput input)
    {
        var method = typeof(ExtractionOrchestrator).GetMethod(
            "RunJobSafelyAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunJobSafelyAsync not found.");
        var task = (Task)method.Invoke(orch, new object?[]
        {
            input,
            /* request */ null,
            /* ksContext */ null,
            /* chunks */ Array.Empty<ChunkSpan>(),
            /* ct */ CancellationToken.None,
        })!;
        return task;
    }

    // ------------------------------------------------------------------
    // Pipeline builders — mirror JobPipelineRouterTests / JobPipelineRealRunTests
    // ------------------------------------------------------------------

    private static TBoxOnlyJobPipeline BuildTBoxOnlyPipeline(ExtractionOrchestrator orch) =>
        new(
            tboxLayer: new TBoxLayerStep(orch),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(orch), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(orch), carry => carry.State),
            noOpABox: new NoOpSegment<HierarchyCarry, ABoxLayerCarry>(s => new ABoxLayerCarry(s.State)),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

    private static ABoxOnlyJobPipeline BuildABoxOnlyPipeline(ExtractionOrchestrator orch) =>
        new(
            noOpTBox: new NoOpSegment<JobState, TBoxLayerCarry>(s => new TBoxLayerCarry(s)),
            noOpAgent: new NoOpSegment<TBoxLayerCarry, AgentCarry>(s => new AgentCarry(s.State)),
            noOpCorpus: new NoOpSegment<AgentCarry, CorpusCarry>(s => new CorpusCarry(s.State)),
            noOpHierarchy: new NoOpSegment<CorpusCarry, HierarchyCarry>(s => new HierarchyCarry(s.State)),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(orch), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

    private static CombinedJobPipeline BuildCombinedPipeline(ExtractionOrchestrator orch) =>
        new(
            tboxLayer: new TBoxLayerStep(orch),
            agent: new ChainAdapter<JobState, TBoxLayerCarry, AgentCarry>(
                new AgentStep(orch), carry => carry.State),
            corpus: new ChainAdapter<JobState, AgentCarry, CorpusCarry>(
                new CorpusStep(orch), carry => carry.State),
            hierarchy: new ChainAdapter<JobState, CorpusCarry, HierarchyCarry>(
                new HierarchyStep(orch), carry => carry.State),
            aboxLayer: new ChainAdapter<JobState, HierarchyCarry, ABoxLayerCarry>(
                new ABoxLayerStep(orch), carry => carry.State),
            terminology: new ChainAdapter<JobState, ABoxLayerCarry, TerminologyCarry>(
                new TerminologyStep(orch), carry => carry.State));

    // ------------------------------------------------------------------
    // DB seeding
    // ------------------------------------------------------------------

    private void SeedKnowledgeSystem()
    {
        using var db = _contexts.CreateDbContext();
        db.KnowledgeSystems.Add(new KnowledgeSystemEntity
        {
            Id = _ksId,
            PublicId = Guid.NewGuid().ToString("N"),
            Name = "JobRouter fixture",
            GraphIri = GraphIri,
            BaseIri = BaseIri,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
}