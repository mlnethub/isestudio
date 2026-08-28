using System.Text.Json;

namespace ISEStudio.Application.Ontology;

/// <summary>
/// A single class entry for the ABox sidebar. <see cref="Count"/> is the
/// number of individuals of this class in the ABox graph (zero when the
/// class has no instances yet).
/// </summary>
public sealed record ClassEntry(
    string Iri,
    string Label,
    int Count);

/// <summary>Wire envelope for <c>GET /abox/classes</c>.</summary>
public sealed record ClassesOut(
    IReadOnlyList<ClassEntry> Classes,
    int Total);

/// <summary>
/// A single individual row for <c>GET /abox/individuals</c>. The
/// <see cref="Label"/> falls back to the local IRI fragment when the
/// ABox graph doesn't carry an <c>rdfs:label</c> yet. The
/// <see cref="Types"/> array carries the same <c>(iri, label)</c> shape
/// as the detail endpoint's <see cref="IndividualOut.Types"/> and the
/// Python baseline (<c>backend/app/ontology/abox.py::list_individuals</c>)
/// so the InstancesPanel can render type chips without a second round-trip
/// to <c>/abox/classes</c>.
/// </summary>
public sealed record IndividualListItem(
    string Iri,
    string Label,
    IReadOnlyList<LabeledIri> Types);

/// <summary>Wire envelope for <c>GET /abox/individuals</c>.</summary>
public sealed record IndividualsOut(
    IReadOnlyList<IndividualListItem> Items,
    int Total);

/// <summary>
/// Lightweight <c>(iri, label)</c> for type / property / target lookups.
/// The <see cref="Label"/> falls back to the local IRI fragment when no
/// <c>rdfs:label</c> is recorded.
/// </summary>
public sealed record LabeledIri(
    string Iri,
    string Label);

/// <summary>
/// One object-property assertion. Mirrors the Python
/// <c>ind["object_assertions"]</c> row.
/// </summary>
public sealed record ObjectAssertionOut(
    string Prop,
    string PropLabel,
    string Target,
    string TargetLabel,
    IReadOnlyList<object> Sources);

/// <summary>
/// One data-property assertion. Mirrors the Python
/// <c>ind["data_assertions"]</c> row.
/// </summary>
public sealed record DataAssertionOut(
    string Prop,
    string PropLabel,
    string Value,
    string? Datatype,
    IReadOnlyList<object> Sources);

/// <summary>
/// Full individual envelope. Mirrors the Python
/// <c>backend/app/ontology/abox.py::get_individual</c> shape minus the
/// <c>sources</c> arrays attached by the Python side from
/// <c>abox_provenance.sources_for</c>. The .NET port defers the
/// provenance attachment to a later slice (ABoxProvenanceService wire-up).
/// </summary>
public sealed record IndividualOut(
    string Iri,
    string Label,
    IReadOnlyList<LabeledIri> Types,
    IReadOnlyList<ObjectAssertionOut> ObjectAssertions,
    IReadOnlyList<DataAssertionOut> DataAssertions);

/// <summary>Return shape for <c>POST /abox/individuals</c> (created individual).</summary>
public sealed record CreateIndividualRequest(
    string Label,
    string ClassIri);

/// <summary>Return shape for <c>POST /abox/individuals/delete</c>.</summary>
public sealed record IndividualRef(string Iri);

/// <summary>Return shape for <c>POST /abox/individuals/delete</c>.</summary>
public sealed record DeleteIndividualResponse(int Removed);

/// <summary>
/// Request body for <c>POST /abox/assertions</c> and
/// <c>POST /abox/assertions/delete</c>. Mirrors the Python
/// <c>backend/app/api/abox.py::Assertion</c> Pydantic model. The
/// <see cref="Kind"/> discriminator picks between object (uses
/// <see cref="Target"/>) and data (uses <see cref="Value"/> +
/// optional <see cref="Datatype"/>) handling.
/// </summary>
public sealed record AssertionRequest(
    string Subject,
    string Prop,
    string Kind,
    string? Target,
    string? Value,
    string? Datatype);

// ----------------------------------------------------------------------
// B7c — validate / reset / fix_violation / validation decisions
// ----------------------------------------------------------------------

/// <summary>
/// Request body for <c>POST /abox/reset</c>. Mirrors the Python
/// <c>ResetAboxRequest</c> Pydantic model &mdash; the <c>Confirm</c>
/// guard prevents a UI typo from wiping every individual in the KS.
/// </summary>
public sealed record ResetAboxRequest(bool Confirm);

/// <summary>
/// Response shape for <c>POST /abox/reset</c>. <see cref="RemovedTriples"/>
/// counts the quads the Oxigraph wipe actually dropped;
/// <see cref="ProvenanceRows"/> + <see cref="ResolutionRows"/> snapshot
/// the SQL-side cleanup sizes so history replay can show how much state
/// the reset swept (mirrors Python <c>reset_abox</c> response).
/// </summary>
public sealed record ResetAboxResponse(
    int RemovedTriples,
    int ProvenanceRows,
    int ResolutionRows);

/// <summary>
/// Request body for <c>POST /abox/validate/fix</c>. Mirrors the Python
/// <c>FixRequest</c> Pydantic model: <see cref="Op"/> is the raw fix
/// payload dispatched by <c>ABoxValidator</c> (kind + per-kind
/// fields like <c>iri</c> / <c>prop</c> / <c>target</c>), and
/// <see cref="Summary"/> becomes the audit row's human-readable
/// summary. The fix-op values are <see cref="JsonElement"/> because
/// <c>System.Text.Json</c> can't reliably coerce nested JSON into
/// <c>object?</c> / <c>string?</c> &mdash; the service unwraps each
/// <see cref="JsonElement"/> via <see cref="FixOpHelpers.AsString"/>.
/// </summary>
public sealed record FixViolationRequest(
    Dictionary<string, JsonElement> Op,
    string? Summary);

/// <summary>
/// Helper for reading strongly-typed values out of a
/// <see cref="FixViolationRequest.Op"/> dictionary where every value is
/// a <see cref="JsonElement"/> because the inbound payload is opaque JSON.
/// </summary>
public static class FixOpHelpers
{
    /// <summary>Return the string value of <paramref name="key"/> or <c>null</c>.</summary>
    public static string? AsString(this Dictionary<string, JsonElement> op, string key)
    {
        if (!op.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString()
            : el.ValueKind == JsonValueKind.Null ? null
            : el.GetRawText().Trim('"');
    }
}

/// <summary>
/// One violation surfaced by <c>ABoxValidator.Validate</c>.
/// Carries the individual ref + a non-empty <see cref="Fixes"/> list of
/// one-click remediation ops (matches Python <c>violations[]</c>
/// shape: <c>{id, type, severity, individual:{iri,label}, summary, fixes}</c>).
/// </summary>
public sealed record ValidationViolationOut(
    string Id,
    string Type,
    string Severity,
    LabeledIri Individual,
    string Summary,
    IReadOnlyList<ViolationFixOut> Fixes);

/// <summary>
/// One-click fix op attached to a violation. <see cref="Op"/> is the
/// raw payload round-tripped to <see cref="FixViolationRequest.Op"/>:
/// at minimum <c>{kind: &lt;op-kind&gt;, ...}</c> with the per-kind
/// fields the dispatcher expects (iri / prop / target / value / class_iri).
/// </summary>
public sealed record ViolationFixOut(
    string Id,
    string Label,
    IReadOnlyDictionary<string, object?> Op);

/// <summary>
/// Aggregate shape for <c>GET /abox/validate</c> (and the response
/// body of <c>POST /abox/validate/fix</c>). Mirrors the Python
/// <c>validate_abox</c> response: <c>{violations, counts, truncated}</c>.
/// </summary>
public sealed record ValidationReportOut(
    IReadOnlyList<ValidationViolationOut> Violations,
    ValidationReportCounts Counts,
    bool Truncated);

/// <summary>Severity-bucketed violation counts on a <see cref="ValidationReportOut"/>.</summary>
public sealed record ValidationReportCounts(int Error, int Warning);

/// <summary>
/// One persisted validation decision &mdash; the agent's (or a human's)
/// remembered preference for how to resolve a recurring violation on a
/// specific data property. Mirrors the Python
/// <c>ValidationDecision</c> SQLModel row.
/// </summary>
public sealed record ValidationDecisionOut(
    Guid Id,
    string PropertyLabel,
    string? PropertyIri,
    string? XsdType,
    string Action,
    string? Reason,
    string? ResolvedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// Response shape for <c>GET /validation/decisions</c>. Mirrors the
/// Python list-decisions response: <c>{items, total}</c>.
/// </summary>
public sealed record ValidationDecisionListOut(
    IReadOnlyList<ValidationDecisionOut> Items,
    int Total);

/// <summary>
/// Response shape for <c>DELETE /validation/decisions/{did}</c>.
/// <see cref="Revoked"/> echoes the row id so the caller can clear
/// the UI row client-side without re-listing.
/// </summary>
public sealed record RevokeValidationDecisionResponse(Guid Revoked);