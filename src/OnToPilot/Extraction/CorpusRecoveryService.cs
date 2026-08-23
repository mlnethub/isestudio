using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Observability;
using OnToPilot.Ontology;

namespace OnToPilot.Extraction;

/// <summary>
/// Job-level corpus recovery pass (Python <c>_recover_rejected_classes</c>):
/// collect every chunk's <see cref="RejectedClass"/> list, group them by
/// label, sample diverse source passages, ask the model to pick the most
/// diagnostic ones, then ask the model to make a final role decision with
/// the selected evidence in hand. Accepted classes are merged into the TBox
/// graph under the same write lock as the per-chunk merge path.
/// </summary>
/// <remarks>
/// <para>The decision helpers are static and side-effect free so the
/// fail-closed boundary can be regression-tested without an LLM or graph —
/// Python <c>_apply_corpus_role_decisions</c> documents the same
/// contract.</para>
/// <para>Rejecting the same label across every chunk's local critic usually
/// means the local view lacked context. With the corpus in hand, the model
/// can adjudicate against evidence the per-chunk pass never saw. This is
/// not a second chance to bypass the boundary critic — every accepted
/// decision still passes exact evidence grounding.</para>
/// </remarks>
public sealed class CorpusRecoveryService
{
    public const string EvidenceSelectorKey = "tbox.boundary.evidence_selector";
    public const string CorpusRecoveryKey = "tbox.boundary.corpus_recovery";

    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly OnToPilotOptions _options;
    private readonly TBoxVerifyService _verify;

    public CorpusRecoveryService(
        IOptions<OnToPilotOptions> options,
        TBoxVerifyService verify)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verify);
        _options = options.Value;
        _verify = verify;
    }

    /// <summary>Resolve one corpus-recovery prompt body — same contract as
    /// <see cref="TBoxVerifyService.ResolveSystemPrompt"/>.</summary>
    public string ResolveSystemPrompt(string promptKey) =>
        _verify.ResolveSystemPrompt(promptKey);

    /// <summary>
    /// Run the corpus recovery pass over one job's per-chunk rejections.
    /// Returns the labels accepted by <see cref="ApplyCorpusRoleDecisions"/>
    /// — the caller is responsible for merging them into the graph under the
    /// appropriate <see cref="StoreWrapper"/> capture. Recovery is silent
    /// when there are no rejected candidates to revisit.
    /// </summary>
    public async Task<CorpusRecoveryResult> RecoverAsync(
        IChatClient chat,
        IReadOnlyList<CorpusRecoveryChunk> perChunk,
        IReadOnlySet<string> existingClassNorms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(perChunk);
        ArgumentNullException.ThrowIfNull(existingClassNorms);

        var candidates = BuildCandidates(perChunk, existingClassNorms);
        if (candidates.Count == 0)
        {
            return CorpusRecoveryResult.Empty;
        }

        var floor = _options.AutoApplyFloor;
        var rows = new List<RecoveredCorpusClass>();
        var structuredSignals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in perChunk)
        {
            foreach (var (norm, reason) in RoleEvidence.StructuredNonTypeValues(chunk.Text))
            {
                structuredSignals.Add(norm);
            }
        }

        foreach (var (normalized, candidate) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = PrepareCorpusEvidence(new Dictionary<string, CorpusCandidate> { [normalized] = candidate });
            var fallback = ApplyCorpusEvidenceSelections(prepared, EmptyPayload());

            var selected = fallback;
            try
            {
                var selectorPayload = await CallAsync(
                    chat, EvidenceSelectorKey,
                    "CANDIDATES AND NUMBERED SOURCE PASSAGES:\n" + ToJson(prepared.Values),
                    "EvidenceSelector",
                    cancellationToken).ConfigureAwait(false);
                selected = ApplyCorpusEvidenceSelections(prepared, selectorPayload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Fail-soft: selector failure falls back to the diverse
                // passages PrepareCorpusEvidence already produced.
            }

            var input = new[]
            {
                new
                {
                    label = candidate.Label,
                    source_passages = selected[normalized].Select(p => new
                    {
                        text = p.Text,
                        earlier_reason = p.EarlierReason,
                        extractor_evidence = p.ExtractorEvidence,
                    }),
                },
            };
            JsonElement payload;
            try
            {
                payload = await CallAsync(
                    chat, CorpusRecoveryKey,
                    "REJECTED CLASS CANDIDATES WITH SELECTED CORPUS EVIDENCE:\n" + ToJson(input),
                    "CorpusRecovery",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The recovery prompt's failure is the chunk's failure for
                // this candidate — Python logs and moves on.
                continue;
            }

            foreach (var accepted in ApplyCorpusRoleDecisions(
                new Dictionary<string, CorpusCandidate> { [normalized] = candidate },
                payload,
                structuredSignals,
                floor))
            {
                rows.Add(new RecoveredCorpusClass(accepted.Label, accepted.Evidence, accepted.ChunkId, accepted.SourceText));
            }
        }

        return new CorpusRecoveryResult(rows);
    }

    // ------------------------------------------------------------------
    // Decision helpers (static, side-effect free)
    // ------------------------------------------------------------------

    /// <summary>
    /// Group rejected candidates across all chunks and produce evidence
    /// windows — Python <c>_recover_rejected_classes</c>'s candidate loop.
    /// Labels already in the graph are skipped; XSD datatype aliases are
    /// rejected up front (Python <c>tbox_guard.canonical_datatype_name</c>);
    /// surface grounding must hold in at least one occurrence.
    /// </summary>
    internal static Dictionary<string, CorpusCandidate> BuildCandidates(
        IReadOnlyList<CorpusRecoveryChunk> perChunk,
        IReadOnlySet<string> existingClassNorms)
    {
        var candidates = new Dictionary<string, CorpusCandidate>(StringComparer.Ordinal);
        foreach (var chunk in perChunk)
        {
            if (string.IsNullOrEmpty(chunk.Text)) continue;
            foreach (var row in chunk.Rejected)
            {
                if (row is null) continue;
                var label = row.Label?.Trim() ?? string.Empty;
                var normalized = TBoxVerifyService.LabelNorm(label);
                if (normalized.Length == 0
                    || existingClassNorms.Contains(normalized)
                    || Vocabulary.CanonicalDatatypeName(label) is not null
                    || !RoleEvidence.SurfaceIsGrounded(chunk.Text, label))
                {
                    continue;
                }
                if (!candidates.TryGetValue(normalized, out var candidate))
                {
                    candidate = new CorpusCandidate(label, new List<CorpusOccurrence>());
                    candidates[normalized] = candidate;
                }
                if (candidate.Occurrences.Any(o => o.ChunkId == chunk.ChunkId)) continue;
                candidate.Occurrences.Add(new CorpusOccurrence(
                    chunk.ChunkId,
                    chunk.Text,
                    row.Evidence ?? string.Empty,
                    row.Reason ?? string.Empty));
            }
        }
        return candidates;
    }

    /// <summary>
    /// Python <c>_prepare_corpus_evidence</c>: for each candidate, sample up to
    /// eight occurrences uniformly across the list and pull up to two label
    /// windows per occurrence. The selector prompt chooses from the
    /// numbered <c>passage_id</c>s; the recovery prompt then sees only those.
    /// </summary>
    internal static Dictionary<string, PreparedCorpusCandidate> PrepareCorpusEvidence(
        IDictionary<string, CorpusCandidate> candidates)
    {
        var prepared = new Dictionary<string, PreparedCorpusCandidate>(StringComparer.Ordinal);
        foreach (var (normalized, candidate) in candidates)
        {
            var passages = new List<PreparedPassage>();
            var seen = new HashSet<(object? ChunkId, string Window)>();
            foreach (var occurrence in EvenlySampled(candidate.Occurrences, 8))
            {
                foreach (var window in LabelEvidenceWindows(occurrence.Text, candidate.Label))
                {
                    var key = (occurrence.ChunkId, window);
                    if (!seen.Add(key)) continue;
                    passages.Add(new PreparedPassage(
                        PassageId: $"p{passages.Count + 1}",
                        ChunkId: occurrence.ChunkId,
                        Text: window,
                        EarlierReason: occurrence.EarlierReason,
                        ExtractorEvidence: occurrence.ExtractorEvidence));
                }
            }
            prepared[normalized] = new PreparedCorpusCandidate(candidate.Label, passages);
        }
        return prepared;
    }

    /// <summary>
    /// Python <c>_apply_corpus_evidence_selections</c>: trust the model's
    /// numbered <c>passage_ids</c> per candidate up to the limit (default
    /// four); fall back to the diverse passages already produced when the
    /// payload is malformed or missing.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<PreparedPassage>> ApplyCorpusEvidenceSelections(
        IDictionary<string, PreparedCorpusCandidate> prepared,
        JsonElement payload,
        int limit = 4)
    {
        var decisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("evidence_selections", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var label = TBoxVerifyService.LabelNorm(DecisionString(row, "label"));
                if (label.Length == 0) continue;
                if (!decisions.ContainsKey(label))
                {
                    decisions[label] = row;
                }
            }
        }

        var selected = new Dictionary<string, IReadOnlyList<PreparedPassage>>(StringComparer.Ordinal);
        foreach (var (normalized, candidate) in prepared)
        {
            var passages = candidate.Passages;
            var byId = new Dictionary<string, PreparedPassage>(StringComparer.Ordinal);
            foreach (var passage in passages)
            {
                byId[passage.PassageId] = passage;
            }
            var decision = decisions.GetValueOrDefault(normalized);
            var chosen = new List<PreparedPassage>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (decision.ValueKind == JsonValueKind.Object
                && decision.TryGetProperty("passage_ids", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String) continue;
                    var key = id.GetString() ?? string.Empty;
                    if (byId.TryGetValue(key, out var passage) && seen.Add(key))
                    {
                        chosen.Add(passage);
                    }
                    if (chosen.Count >= limit) break;
                }
            }
            selected[normalized] = chosen.Count > 0 ? chosen : EvenlySampled(passages, limit);
        }
        return selected;
    }

    /// <summary>
    /// Python <c>_apply_corpus_role_decisions</c>: fail-closed boundary
    /// applied to the selected corpus evidence. Every accepted class must
    /// (1) pass the structured-signal independent-evidence check, (2) not
    /// have an explicit "instance of X" declaration in any occurrence,
    /// (3) not be an XSD datatype alias, (4) have a keep=true type decision
    /// at or above the floor, and (5) be supported by at least one
    /// occurrence whose text grounds both the label and the evidence.
    /// </summary>
    internal static IReadOnlyList<AcceptedCorpusClass> ApplyCorpusRoleDecisions(
        IDictionary<string, CorpusCandidate> candidates,
        JsonElement payload,
        IReadOnlySet<string> structuredNonTypeSignals,
        double floor)
    {
        var decisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("class_decisions", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var label = TBoxVerifyService.LabelNorm(DecisionString(row, "label"));
                if (label.Length == 0) continue;
                if (!decisions.ContainsKey(label))
                {
                    decisions[label] = row;
                }
            }
        }

        var accepted = new List<AcceptedCorpusClass>();
        foreach (var (normalized, candidate) in candidates)
        {
            decisions.TryGetValue(normalized, out var decision);
            var label = candidate.Label.Trim();
            var evidence = DecisionString(decision, "evidence");
            var support = candidate.Occurrences.FirstOrDefault(o =>
                RoleEvidence.SurfaceIsGrounded(o.Text, label)
                && RoleEvidence.EvidenceIsGrounded(o.Text, evidence));
            var explicitIndividual = candidate.Occurrences.Any(o =>
                RoleEvidence.HasExplicitIndividualDeclaration(o.Text, label));
            var labelNorm = RoleEvidence.Normalize(label);
            var inStructuredSignals = structuredNonTypeSignals.Contains(labelNorm);

            if (decision.ValueKind != JsonValueKind.Object
                || !DecisionBool(decision, "keep")
                || DecisionString(decision, "role").Trim().ToLowerInvariant() != RoleEvidence.RoleType
                || DecisionConfidence(decision) < floor
                || Vocabulary.CanonicalDatatypeName(label) is not null
                || (inStructuredSignals && !HasIndependentTypeEvidence(label, decision))
                || explicitIndividual
                || support is null)
            {
                continue;
            }

            accepted.Add(new AcceptedCorpusClass(
                label,
                evidence,
                support.ChunkId,
                support.Text));
        }
        return accepted;
    }

    private static bool HasIndependentTypeEvidence(string label, JsonElement decision)
    {
        var evidence = DecisionString(decision, "evidence").Trim();
        if (evidence.Length == 0) return false;
        return !RoleEvidence.StructuredNonTypeValues(evidence).ContainsKey(RoleEvidence.Normalize(label));
    }

    // ------------------------------------------------------------------
    // Python helpers (verbatim ports)
    // ------------------------------------------------------------------

    private static List<T> EvenlySampled<T>(IReadOnlyList<T> rows, int limit)
    {
        if (limit <= 0 || rows.Count == 0) return new List<T>();
        if (rows.Count <= limit) return rows.ToList();
        if (limit == 1) return new List<T> { rows[rows.Count / 2] };
        var indexes = new SortedSet<int>();
        for (var i = 0; i < limit; i++)
        {
            indexes.Add((int)Math.Round((double)i * (rows.Count - 1) / (limit - 1)));
        }
        return indexes.Select(i => rows[i]).ToList();
    }

    private static List<string> LabelEvidenceWindows(string text, string label, int radius = 320, int limit = 2)
    {
        var localPart = label.IndexOf(':') >= 0 ? label[(label.IndexOf(':') + 1)..] : label;
        var forms = new List<string>();
        foreach (var form in new[] { label, localPart, label.Replace('_', ' ') })
        {
            if (!string.IsNullOrEmpty(form)
                && !forms.Any(f => string.Equals(f, form, StringComparison.OrdinalIgnoreCase)))
            {
                forms.Add(form);
            }
        }
        var positions = forms
            .SelectMany(form => Regex.Matches(text, Regex.Escape(form), RegexOptions.IgnoreCase).Cast<Match>())
            .Select(m => m.Index)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        if (positions.Count == 0)
        {
            return text.Length == 0 ? new List<string>() : new List<string> { text[..Math.Min(text.Length, 2 * radius)] };
        }
        var sampled = EvenlySampled(positions, limit);
        var windows = new List<string>();
        foreach (var position in sampled)
        {
            var start = Math.Max(0, position - radius);
            var end = Math.Min(text.Length, position + label.Length + radius);
            var window = text[start..end];
            if (window.Length > 0 && !windows.Contains(window))
            {
                windows.Add(window);
            }
        }
        return windows;
    }

    // ------------------------------------------------------------------
    // LLM plumbing
    // ------------------------------------------------------------------

    private async Task<JsonElement> CallAsync(
        IChatClient chat,
        string promptKey,
        string user,
        string stage,
        CancellationToken cancellationToken)
    {
        var systemPrompt = ResolveSystemPrompt(promptKey);
        var provider = chat.GetService<ChatClientMetadata>()?.ProviderName ?? "unknown";
        var model = chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown";

        return await Telemetry.LlmSource.WithLlmActivity(
            operationName: $"Llm.TBoxCorpus.{stage}",
            provider: provider,
            model: model,
            action: async ct =>
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, user),
                };
                var response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                if (!ExtractionDeltaParser.TryReadObject(response.Text, out var root))
                {
                    throw new InvalidOperationException(
                        $"TBox corpus {stage.ToLowerInvariant()} did not return a JSON object");
                }
                return root;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement EmptyPayload() => JsonDocument.Parse("{}").RootElement.Clone();

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, Snake);

    // ------------------------------------------------------------------
    // JSON helpers
    // ------------------------------------------------------------------

    private static IEnumerable<JsonElement> ArrayItems(JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object) yield break;
        if (!parent.TryGetProperty(field, out var array)) yield break;
        if (array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }

    private static string DecisionString(JsonElement decision, string field)
    {
        if (decision.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!decision.TryGetProperty(field, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static bool DecisionBool(JsonElement decision, string field)
    {
        if (decision.ValueKind != JsonValueKind.Object) return false;
        if (!decision.TryGetProperty(field, out var raw)) return false;
        return raw.ValueKind == JsonValueKind.True;
    }

    private static double DecisionConfidence(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object) return 0.0;
        if (!decision.TryGetProperty("confidence", out var raw)) return 0.0;
        double value;
        switch (raw.ValueKind)
        {
            case JsonValueKind.Number: value = raw.GetDouble(); break;
            case JsonValueKind.String:
                if (!double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return 0.0;
                }
                break;
            case JsonValueKind.True: return 1.0;
            default: return 0.0;
        }
        if (!double.IsFinite(value)) return 0.0;
        return Math.Clamp(value, 0.0, 1.0);
    }
}

/// <summary>One chunk's contribution to the corpus recovery pass.</summary>
public sealed record CorpusRecoveryChunk(
    int ChunkId,
    string Text,
    IReadOnlyList<RejectedClass> Rejected);

internal sealed record CorpusCandidate(string Label, List<CorpusOccurrence> Occurrences);

internal sealed record CorpusOccurrence(
    int? ChunkId,
    string Text,
    string ExtractorEvidence,
    string EarlierReason);

internal sealed record PreparedCorpusCandidate(
    string Label,
    IReadOnlyList<PreparedPassage> Passages);

internal sealed record PreparedPassage(
    string PassageId,
    int? ChunkId,
    string Text,
    string EarlierReason,
    string ExtractorEvidence);

/// <summary>Outcome of one job's corpus recovery pass.</summary>
public sealed record CorpusRecoveryResult(IReadOnlyList<RecoveredCorpusClass> Classes)
{
    public static CorpusRecoveryResult Empty { get; } = new(Array.Empty<RecoveredCorpusClass>());
}

/// <summary>
/// A class the corpus recovery pass accepted. The caller merges it into the
/// graph under the same write lock as the per-chunk path; the recovery
/// service is responsible only for the role decision.
/// </summary>
public sealed record RecoveredCorpusClass(string Label, string Evidence, int? ChunkId, string SourceText);

internal sealed record AcceptedCorpusClass(string Label, string Evidence, int? ChunkId, string SourceText);