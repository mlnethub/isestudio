using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobStateMutationTests
{
    private const string GraphIri = "http://test.local/ks/jobstate";
    private const string BaseIri = GraphIri + "/onto#";

    /// <summary>
    /// Test-time <see cref="KsContext"/>. JobInput/JobState carry a real
    /// instance in production; the unit tests need only the graph IRIs the
    /// <see cref="KsContext"/> derives so the fields have something to
    /// project.
    /// </summary>
    private static KsContext KsContext => new(GraphIri, BaseIri);

    /// <summary>
    /// Test-time <see cref="ExtractionRequest"/>. The phase runners do not
    /// fire from these tests, so the request's content beyond the IRI is
    /// irrelevant.
    /// </summary>
    private static ExtractionRequest Request => new(
        KnowledgeSystemId: Guid.NewGuid(),
        BlobSha: string.Empty,
        FileName: "test.txt",
        Provider: "openai",
        Model: "fake-model",
        Endpoint: "https://fake.test/v1",
        ApiKey: null,
        ConcurrencyLimit: 2);

    /// <summary>
    /// Empty state for mutation-shape tests. Slice 5 Task 4 R11 added four
    /// per-job closure fields (<see cref="JobState.KsContext"/>,
    /// <see cref="JobState.Request"/>, <see cref="JobState.Chunks"/>,
    /// <see cref="JobState.PerChunk"/>); the unit tests pass inert stand-ins
    /// so the JobInput record's positional ctor keeps its 11-arg shape
    /// readable.
    /// </summary>
    private static JobState EmptyState() => JobState.From(new JobInput(
        JobId: Guid.NewGuid(),
        KnowledgeSystemId: Guid.NewGuid(),
        ChunkIds: new[] { 1, 2, 3 },
        Chat: null!,
        Kind: JobKind.Combined,
        InitialVocabulary: null,
        CancellationToken: CancellationToken.None,
        KsContext: KsContext,
        Request: Request,
        Chunks: Array.Empty<ChunkSpan>(),
        PerChunk: Array.Empty<ChunkVerifyOutcome>()));

    [Fact]
    [Trait("Category", "Extraction")]
    public void From_ProjectsAllJobInputFields()
    {
        var ksId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var chunks = new[] { new ChunkSpan(1, "text", 0, 4, 1) };
        var perChunk = new[] { new ChunkVerifyOutcome(1, "text", Array.Empty<RejectedClass>()) };
        var input = new JobInput(
            jobId, ksId, new[] { 1 },
            null!, JobKind.TBoxOnly, new[] { "x" }, CancellationToken.None,
            KsContext, Request, chunks, perChunk);
        var state = JobState.From(input);

        Assert.Equal(input.JobId, state.JobId);
        Assert.Equal(input.Kind, state.Kind);
        Assert.Equal(input.InitialVocabulary, state.InitialVocabulary);
        Assert.Equal(input.KsContext, state.KsContext);
        Assert.Equal(input.Request, state.Request);
        Assert.Equal(input.Chunks, state.Chunks);
        Assert.Equal(input.PerChunk, state.PerChunk);
        Assert.True(state.Succeeded);
        Assert.False(state.ShouldSkipRemaining);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void With_AddsError_PropagatesSkipFlag()
    {
        var state = EmptyState();
        var failed = state with { Error = "boom" };

        Assert.False(failed.Succeeded);
        Assert.True(failed.ShouldSkipRemaining);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void IsImmutable_OriginalStateUnchangedAfterWith()
    {
        var state = EmptyState();
        var _ = state with { ProcessedChunks = 42, TBoxChunkResults = new[] {
            new ChunkResult(1, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>()) } };

        Assert.Equal(0, state.ProcessedChunks);
        Assert.Empty(state.TBoxChunkResults);
    }

    [Fact]
    [Trait("Category", "Extraction")]
    public void FromJobState_ProjectsAllPhaseOutputs()
    {
        var state = EmptyState() with
        {
            TBoxChunkResults = new[] { new ChunkResult(1, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>()) },
            ABoxChunkResults = new[] { new ChunkResult(2, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>()) },
            Terminology = new JobTerminology(1, 2, 3, null),
            ProcessedChunks = 5,
            Error = null,
        };
        var result = JobResult.FromJobState(state);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.ProcessedChunks);
        Assert.Single(result.TBoxChunkResults);
        Assert.Single(result.ABoxChunkResults);
        Assert.NotNull(result.Terminology);
        Assert.Equal(1, result.Terminology!.TermsAdded);
    }
}
