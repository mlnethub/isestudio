using ISEStudio.Extraction.Dovetail.Job;
using Xunit;

namespace ISEStudio.Tests.Extraction.Dovetail.Job;

public sealed class JobStateMutationTests
{
    private static JobState EmptyState() => JobState.From(new JobInput(
        JobId: Guid.NewGuid(),
        KnowledgeSystemId: Guid.NewGuid(),
        ChunkIds: new[] { 1, 2, 3 },
        Chat: null!,
        Kind: JobKind.Combined,
        InitialVocabulary: null,
        CancellationToken: CancellationToken.None));

    [Fact]
    [Trait("Category", "Extraction")]
    public void From_ProjectsAllJobInputFields()
    {
        var input = new JobInput(
            Guid.NewGuid(), Guid.NewGuid(), new[] { 1 },
            null!, JobKind.TBoxOnly, new[] { "x" }, CancellationToken.None);
        var state = JobState.From(input);

        Assert.Equal(input.JobId, state.JobId);
        Assert.Equal(input.Kind, state.Kind);
        Assert.Equal(input.InitialVocabulary, state.InitialVocabulary);
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