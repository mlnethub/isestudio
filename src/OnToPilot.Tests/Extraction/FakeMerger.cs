using OnToPilot.Extraction;
using OnToPilot.Ontology;

namespace OnToPilot.Tests.Extraction;

/// <summary>
/// <see cref="IExtractionMerger"/> decorator that can be primed to throw on
/// the next merge. Wrapping the real <see cref="ExtractionMerger"/> (rather
/// than replacing it) keeps the success path exercising production merge
/// behaviour — the RDF writes really happen — so the revert assertion in
/// <c>Failed_merge_reverts_rdf_and_marks_job_failed</c> is meaningful:
/// the merger writes quads first and only then throws.
/// </summary>
public sealed class FakeMerger : IExtractionMerger
{
    private readonly IExtractionMerger _inner;
    private Exception? _failure;

    public FakeMerger(IExtractionMerger inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Number of merge calls that ran (including failing ones).</summary>
    public int MergeCount { get; private set; }

    /// <summary>
    /// Make every subsequent merge throw <paramref name="exception"/> after
    /// the inner merger has already written its quads. Pass <c>null</c> to
    /// go back to succeeding.
    /// </summary>
    public void FailWith(Exception? exception) => _failure = exception;

    /// <summary>Clear the primed failure and the call counter.</summary>
    public void Reset()
    {
        _failure = null;
        MergeCount = 0;
    }

    /// <inheritdoc />
    public ExtractionMergeResult MergeTBox(KsContext ks, TBoxDelta delta, TBoxVerifyResult? verify)
    {
        MergeCount++;
        var result = _inner.MergeTBox(ks, delta, verify);
        if (_failure is not null) throw _failure;
        return result;
    }

    /// <inheritdoc />
    public ExtractionMergeResult MergeABox(KsContext ks, ABoxDelta delta)
    {
        MergeCount++;
        var result = _inner.MergeABox(ks, delta);
        if (_failure is not null) throw _failure;
        return result;
    }
}
