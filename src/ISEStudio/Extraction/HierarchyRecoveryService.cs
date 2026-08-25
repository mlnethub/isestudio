using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Observability;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction;

/// <summary>
/// One chunk's independent hierarchy recovery pass (Python
/// <c>_recover_hierarchy_one</c>): given the final class vocabulary and the
/// chunk text, propose source-grounded explicit parents plus any missing
/// intermediate classes. Every accepted class re-runs the boundary critic
/// through <see cref="TBoxVerifyService.VerifyAsync"/>; every accepted edge
/// runs through <see cref="VerifySubclassCandidatesAsync"/>. Only
/// already-admitted endpoints survive.
/// </summary>
/// <remarks>
/// Recovery runs after the TBox phase is committed so the graph's class
/// index the prompt sees reflects every chunk's contribution. The proposed
/// classes are filtered to those that back at least one accepted edge —
/// Python's <c>used_accepted_new_norms</c> carry-over — so the helper never
/// writes a dangling class.
/// </remarks>
public sealed class HierarchyRecoveryService
{
    public const string HierarchyCriticKey = "tbox.hierarchy.critic";
    public const string HierarchyRecoveryKey = "tbox.hierarchy.recovery";

    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ISEStudioOptions _options;
    private readonly TBoxVerifyService _verify;

    public HierarchyRecoveryService(
        IOptions<ISEStudioOptions> options,
        TBoxVerifyService verify)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verify);
        _options = options.Value;
        _verify = verify;
    }

    /// <summary>Resolve one hierarchy-recovery prompt body — same contract as
    /// <see cref="TBoxVerifyService.ResolveSystemPrompt"/>.</summary>
    public string ResolveSystemPrompt(string promptKey) =>
        _verify.ResolveSystemPrompt(promptKey);

    /// <summary>
    /// Recover explicit hierarchy edges (and any supporting super-classes)
    /// for one chunk. Returns the accepted classes plus accepted edges.
    /// </summary>
    public async Task<HierarchyRecoveryResult> RecoverAsync(
        IChatClient chat,
        string text,
        IReadOnlyList<string> allowedLabels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowedLabels);

        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in allowedLabels)
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            canonical[TBoxVerifyService.LabelNorm(label)] = label.Trim();
        }
        if (canonical.Count == 0)
        {
            return HierarchyRecoveryResult.Empty;
        }

        var payload = await CallAsync(
            chat, HierarchyRecoveryKey,
            SourceBlock(text) + "EXISTING CLASSES:\n" + ToJson(canonical.Values),
            "HierarchyRecovery",
            cancellationToken).ConfigureAwait(false);

        var proposedClasses = new List<ProposedClass>();
        var newCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var row in ArrayItems(payload, "classes"))
            {
                var label = DecisionString(row, "label");
                if (label.Length == 0) label = DecisionString(row, "name");
                var normalized = TBoxVerifyService.LabelNorm(label);
                var evidence = DecisionString(row, "evidence");
                if (normalized.Length == 0
                    || canonical.ContainsKey(normalized)
                    || newCanonical.ContainsKey(normalized)
                    || !RoleEvidence.SurfaceIsGrounded(text, label)
                    || !RoleEvidence.EvidenceIsGrounded(text, evidence))
                {
                    continue;
                }
                newCanonical[normalized] = label.Trim();
                proposedClasses.Add(new ProposedClass(label.Trim(), evidence));
            }
        }

        var proposedEdges = new List<ProposedEdge>();
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var row in ArrayItems(payload, "subclass_of"))
            {
                var subNorm = TBoxVerifyService.LabelNorm(DecisionString(row, "sub"));
                var superNorm = TBoxVerifyService.LabelNorm(DecisionString(row, "super"));
                var sub = canonical.GetValueOrDefault(subNorm);
                var sup = canonical.GetValueOrDefault(superNorm)
                    ?? newCanonical.GetValueOrDefault(superNorm);
                var evidence = DecisionString(row, "evidence");
                if (string.IsNullOrEmpty(sub)
                    || string.IsNullOrEmpty(sup)
                    || sub == sup
                    || !RoleEvidence.EvidenceIsGrounded(text, evidence))
                {
                    continue;
                }
                proposedEdges.Add(new ProposedEdge(sub, sup!, evidence));
            }
        }

        if (proposedEdges.Count == 0)
        {
            return HierarchyRecoveryResult.Empty;
        }

        // Only classes whose label is the super-end of at least one proposed
        // edge can land — the rest would be dangling. Python carries the
        // surviving ones into the critic chain.
        var usedNewNorms = proposedEdges
            .Select(e => TBoxVerifyService.LabelNorm(e.Super))
            .Where(newCanonical.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);
        var filteredClasses = proposedClasses
            .Where(c => usedNewNorms.Contains(TBoxVerifyService.LabelNorm(c.Label)))
            .ToList();

        var verifiedClasses = new List<ClassMutation>();
        if (filteredClasses.Count > 0)
        {
            var proposedDelta = new TBoxDelta(
                filteredClasses.Select(c => new ClassMutation(c.Label, Comment: null, RoleVerified: false)).ToList(),
                Array.Empty<PropertyMutation>(),
                Array.Empty<PropertyMutation>(),
                Array.Empty<AxiomMutation>());
            var verified = await _verify.VerifyAsync(chat, text, proposedDelta, cancellationToken)
                .ConfigureAwait(false);
            verifiedClasses.AddRange(verified.Delta.Classes);
        }

        var allowedNorms = new HashSet<string>(canonical.Keys, StringComparer.Ordinal);
        foreach (var c in verifiedClasses)
        {
            allowedNorms.Add(TBoxVerifyService.LabelNorm(c.Label));
        }

        var admissible = proposedEdges
            .Where(e =>
            {
                var key = (TBoxVerifyService.LabelNorm(e.Sub), TBoxVerifyService.LabelNorm(e.Super));
                return allowedNorms.Contains(key.Item1) && allowedNorms.Contains(key.Item2);
            })
            .ToList();

        var acceptedEdges = await VerifySubclassCandidatesAsync(
            chat, text, admissible, allowedNorms, cancellationToken).ConfigureAwait(false);

        var usedAcceptedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in acceptedEdges)
        {
            var supNorm = TBoxVerifyService.LabelNorm(edge.Super);
            if (newCanonical.ContainsKey(supNorm))
            {
                usedAcceptedNew.Add(supNorm);
            }
        }

        var finalClasses = verifiedClasses
            .Where(c => usedAcceptedNew.Contains(TBoxVerifyService.LabelNorm(c.Label)))
            .ToList();

        return new HierarchyRecoveryResult(finalClasses, acceptedEdges);
    }

    /// <summary>
    /// Python <c>_verify_subclass_candidates</c>: ask the hierarchy critic for
    /// a fresh judgement on every proposed edge, then apply
    /// <see cref="ApplySubclassDecisions"/> with <paramref name="allowedNorms"/>
    /// as the universe.
    /// </summary>
    private async Task<IReadOnlyList<RecoveredEdge>> VerifySubclassCandidatesAsync(
        IChatClient chat,
        string text,
        IReadOnlyList<ProposedEdge> proposed,
        IReadOnlySet<string> allowedNorms,
        CancellationToken cancellationToken)
    {
        if (proposed.Count == 0)
        {
            return Array.Empty<RecoveredEdge>();
        }

        var rows = proposed.Select(p => new
        {
            sub = p.Sub,
            super = p.Super,
            evidence = p.Evidence,
        }).ToList();
        var payload = await CallAsync(
            chat, HierarchyCriticKey,
            SourceBlock(text) + "PROPOSED SUBCLASS EDGES:\n" + ToJson(rows),
            "HierarchyCritic",
            cancellationToken).ConfigureAwait(false);

        return ApplySubclassDecisions(text, proposed, payload, allowedNorms);
    }

    /// <summary>
    /// Python <c>_apply_subclass_decisions</c>: accept an edge only when its
    /// critic decision says <c>keep is True</c> at or above the auto-accept
    /// floor, the evidence is grounded in the source, and both endpoints are
    /// in <paramref name="allowedNorms"/>. Endpoints outside the universe are
    /// silently dropped — the helper trusts nothing that has not been admitted.
    /// </summary>
    internal static IReadOnlyList<RecoveredEdge> ApplySubclassDecisions(
        string text,
        IReadOnlyList<ProposedEdge> proposed,
        JsonElement payload,
        IReadOnlySet<string> allowedNorms)
    {
        var decisions = new Dictionary<(string Sub, string Super), JsonElement>();
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var row in ArrayItems(payload, "subclass_decisions"))
            {
                var subNorm = TBoxVerifyService.LabelNorm(SubclassField(row, "sub", "child", "subclass"));
                var superNorm = TBoxVerifyService.LabelNorm(SubclassField(row, "super", "parent", "superclass"));
                if (subNorm.Length == 0 || superNorm.Length == 0) continue;
                var key = (subNorm, superNorm);
                if (!decisions.ContainsKey(key))
                {
                    decisions[key] = row;
                }
            }
        }

        var accepted = new List<RecoveredEdge>();
        foreach (var edge in proposed)
        {
            var key = (TBoxVerifyService.LabelNorm(edge.Sub), TBoxVerifyService.LabelNorm(edge.Super));
            if (!allowedNorms.Contains(key.Item1) || !allowedNorms.Contains(key.Item2))
            {
                continue;
            }
            if (!decisions.TryGetValue(key, out var decision))
            {
                continue;
            }
            var evidence = DecisionString(decision, "evidence");
            if (!DecisionBool(decision, "keep")
                || DecisionConfidence(decision) < SubclassFloor
                || !RoleEvidence.EvidenceIsGrounded(text, evidence))
            {
                continue;
            }
            accepted.Add(new RecoveredEdge(edge.Sub, edge.Super, evidence));
        }
        return accepted;
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
            operationName: $"Llm.TBoxHierarchy.{stage}",
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
                        $"TBox hierarchy {stage.ToLowerInvariant()} did not return a JSON object");
                }
                return root;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string SourceBlock(string text) =>
        $"SOURCE TEXT:\n\"\"\"\n{text}\n\"\"\"\n\n";

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, Snake);

    // ------------------------------------------------------------------
    // JSON helpers
    // ------------------------------------------------------------------

    private const double SubclassFloor = 0.85;

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

    /// <summary>
    /// Python <c>_subclass_pair</c> fallbacks: <c>sub</c>/<c>child</c>/<c>subclass</c>
    /// and <c>super</c>/<c>parent</c>/<c>superclass</c>.
    /// </summary>
    private static string SubclassField(JsonElement decision, params string[] fields)
    {
        if (decision.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var field in fields)
        {
            var s = DecisionString(decision, field);
            if (s.Length > 0) return s;
        }
        return string.Empty;
    }

    // ------------------------------------------------------------------
    // Wire DTOs
    // ------------------------------------------------------------------

    internal sealed record ProposedClass(string Label, string Evidence);

    internal sealed record ProposedEdge(string Sub, string Super, string Evidence);
}

/// <summary>Outcome of one chunk's hierarchy recovery pass.</summary>
public sealed record HierarchyRecoveryResult(
    IReadOnlyList<ClassMutation> Classes,
    IReadOnlyList<RecoveredEdge> Edges)
{
    public static HierarchyRecoveryResult Empty { get; } =
        new(Array.Empty<ClassMutation>(), Array.Empty<RecoveredEdge>());
}

/// <summary>A subclass edge the hierarchy recovery pass accepted.</summary>
public sealed record RecoveredEdge(string Sub, string Super, string Evidence);