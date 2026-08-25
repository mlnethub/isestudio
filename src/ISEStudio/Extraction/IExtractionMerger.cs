using ISEStudio.Ontology;

namespace ISEStudio.Extraction;

/// <summary>
/// Per-chunk merge counters, rolled up onto the job row. Mirrors the
/// summary dictionaries the Python workers assembled
/// (<c>classes_added</c>, <c>properties_added</c>, <c>axioms_added</c>,
/// <c>created</c>, <c>assertions</c>, <c>queued</c>, <c>unknown_classes</c>).
/// </summary>
/// <param name="ClassesAdded">OWL classes newly declared.</param>
/// <param name="PropertiesAdded">OWL object + datatype properties newly declared.</param>
/// <param name="AxiomsAdded">Class-level axioms newly asserted.</param>
/// <param name="IndividualsAdded">Individuals newly minted.</param>
/// <param name="AssertionsAdded">Data + object assertions newly written.</param>
/// <param name="PendingAdded">Mentions that could not be resolved and were queued.</param>
/// <param name="UnknownClasses"><c>{label: times_seen}</c> for class labels absent from the TBox.</param>
/// <param name="ProvenanceKeys">
/// Canonical axiom / fact keys (see <see cref="StatementProvenanceService"/>)
/// this merge produced, for the provenance rows the API layer writes.
/// </param>
/// <param name="RejectedClasses">
/// TBox candidate classes rejected by the verify pipeline (critic / adjudicator /
/// denotation) and therefore dropped from the graph. Carried forward to the
/// post-extraction <c>corpus recovery</c> pass so the model gets a second
/// look with cross-chunk evidence (Python <c>_recover_rejected_classes</c>).
/// </param>
/// <param name="RecoveredClasses">
/// Class labels recovered by the verify pipeline's denotation phase as a
/// suffix replacement (Python <c>_role_recoveries</c>). Reported back to the
/// job row's phase log alongside <see cref="RejectedClasses"/>.
/// </param>
public sealed record ExtractionMergeResult(
    int ClassesAdded,
    int PropertiesAdded,
    int AxiomsAdded,
    int IndividualsAdded,
    int AssertionsAdded,
    int PendingAdded,
    IReadOnlyDictionary<string, int> UnknownClasses,
    IReadOnlyList<string> ProvenanceKeys,
    IReadOnlyList<RejectedClass> RejectedClasses,
    IReadOnlyList<RecoveredClass> RecoveredClasses)
{
    /// <summary>An all-zero result.</summary>
    public static ExtractionMergeResult Empty { get; } = new(
        0, 0, 0, 0, 0, 0,
        new Dictionary<string, int>(StringComparer.Ordinal),
        Array.Empty<string>(),
        Array.Empty<RejectedClass>(),
        Array.Empty<RecoveredClass>());

    /// <summary>Sum two results, merging the unknown-class histograms.</summary>
    public ExtractionMergeResult Add(ExtractionMergeResult other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var unknown = new Dictionary<string, int>(UnknownClasses, StringComparer.Ordinal);
        foreach (var (label, count) in other.UnknownClasses)
        {
            unknown[label] = unknown.TryGetValue(label, out var seen) ? seen + count : count;
        }

        var provenance = new List<string>(ProvenanceKeys.Count + other.ProvenanceKeys.Count);
        provenance.AddRange(ProvenanceKeys);
        provenance.AddRange(other.ProvenanceKeys);

        var rejected = new List<RejectedClass>(RejectedClasses.Count + other.RejectedClasses.Count);
        rejected.AddRange(RejectedClasses);
        rejected.AddRange(other.RejectedClasses);

        var recovered = new List<RecoveredClass>(RecoveredClasses.Count + other.RecoveredClasses.Count);
        recovered.AddRange(RecoveredClasses);
        recovered.AddRange(other.RecoveredClasses);

        return new ExtractionMergeResult(
            ClassesAdded + other.ClassesAdded,
            PropertiesAdded + other.PropertiesAdded,
            AxiomsAdded + other.AxiomsAdded,
            IndividualsAdded + other.IndividualsAdded,
            AssertionsAdded + other.AssertionsAdded,
            PendingAdded + other.PendingAdded,
            unknown,
            provenance,
            rejected,
            recovered);
    }
}

/// <summary>
/// Applies an extracted delta to the RDF store.
///
/// <para><b>Locking contract (load-bearing).</b> Implementations must write
/// through <see cref="StoreWrapper"/> primitives only, and must
/// <em>never</em> open their own <see cref="StoreWrapper.CaptureAsync(string, bool, TimeSpan?, CancellationToken)"/>.
/// <see cref="ExtractionOrchestrator"/> already holds an exclusive capture on
/// the target graph when it calls these methods, and
/// <see cref="GraphWriteCoordinator"/> uses
/// <see cref="System.Threading.LockRecursionPolicy.NoRecursion"/> — a nested
/// capture on the same graph raises
/// <see cref="GraphWriteConflictException"/> instead of deadlocking. That is
/// why the merge path bypasses <see cref="OntologyEditor"/> (which takes its
/// own capture per edit) and goes through
/// <see cref="SchemaBuilder.BuildMutation"/> + <see cref="StoreWrapper.AddQuads"/>
/// instead.</para>
///
/// <para>The interface exists so tests can inject a merger that fails
/// deterministically and assert the orchestrator reverts the RDF writes and
/// marks the SQL row failed.</para>
/// </summary>
public interface IExtractionMerger
{
    /// <summary>
    /// Merge one chunk's schema candidates into the TBox graph. The verify
    /// pipeline's rejection / recovery list is forwarded so the result row
    /// can carry the cross-chunk evidence the corpus recovery pass needs.
    /// Pass <c>null</c> when no verifier is wired (hand-built test
    /// orchestrators).
    /// </summary>
    ExtractionMergeResult MergeTBox(KsContext ks, TBoxDelta delta, TBoxVerifyResult? verify);

    /// <summary>Merge one chunk's instance candidates into the ABox graph.</summary>
    ExtractionMergeResult MergeABox(KsContext ks, ABoxDelta delta);
}
