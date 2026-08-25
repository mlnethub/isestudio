using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Extraction;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Llm;
using ISEStudio.Ontology;
using ISEStudio.Prompts;
using OntoNamedNode = Oxigraph.NamedNode;

namespace ISEStudio.Conflicts;

/// <summary>
/// Agentic conflict triage. .NET port of
/// <c>backend/app/ontology/conflict_agent.py</c>.
///
/// <para>After conflict detection, the agent looks at open
/// <c>duplicate</c> / <c>predicate_specialization</c> conflicts and asks
/// the chat model to pick the best available resolution through a short
/// ReAct tool loop (<c>get_neighborhood</c> / <c>finish</c>). Structural
/// conflicts (cycles, disjoint contradictions) are never touched by the
/// agent.</para>
///
/// <para>Product decision (P3-11): decisions at or above
/// <see cref="ISEStudioOptions.AutoApplyFloor"/> auto-apply — the editor
/// op runs inside <see cref="OntologyEditor.ApplyEditAsync"/>'s own
/// capture, the conflict row flips to <c>resolved</c>, and a
/// <c>conflict.resolve</c> audit row (actor <c>conflict-agent</c>,
/// <c>agent: true</c>) records the TBox diff plus a grouped ABox row
/// when the edit cascaded into instance data. Decisions below the floor
/// only attach a <c>payload.recommendation</c> for human one-click
/// confirmation. The Python backend keeps <c>AUTO_APPLY_TYPES</c> empty
/// so its apply branch is unreachable; the .NET product decision enables
/// it behind the confidence floor (shared with the structure agent). A
/// failed apply leaves the conflict open and unattached (Python
/// <c>logger.warning + continue</c>).</para>
///
/// <para>Every LLM hiccup (unparsable reply, provider error, missing
/// provider) leaves the conflict untouched for a human instead of failing
/// the surrounding detect request — the Python worker catches per-conflict
/// and merely logs.</para>
///
/// <para>The agent is registered as a <see cref="ServiceLifetime.Scoped"/>
/// service so the dispatcher can resolve it per request and the EF
/// <see cref="ISEStudioDbContext"/> flows through naturally.</para>
/// </summary>
public sealed class ConflictAgent
{
    /// <summary>
    /// Prompt registry key this agent consumes. Matches the Python
    /// backend's <c>prompt_config.register(key="conflict.resolution", ...)</c>
    /// entry in <c>backend/app/ontology/conflict_agent.py</c>.
    /// </summary>
    public const string PromptKey = "conflict.resolution";

    /// <summary>
    /// Conflict types the agent may triage. Structural conflicts are
    /// intentionally excluded — those need human judgement (Python
    /// <c>AUTO_TYPES</c>).
    /// </summary>
    private static readonly string[] AutoTypes = { "duplicate", "predicate_specialization" };

    private readonly IChatClientFactory _chatFactory;
    private readonly ISEStudioDbContext _db;
    private readonly StoreWrapper? _store;
    private readonly ExtractionJobStore? _jobs;
    private readonly LegacyIdAllocator? _allocator;
    private readonly ISEStudioOptions _options;

    public ConflictAgent(
        IChatClientFactory chatFactory,
        ISEStudioDbContext db,
        StoreWrapper? store = null,
        ExtractionJobStore? jobs = null,
        IOptions<ISEStudioOptions>? options = null,
        LegacyIdAllocator? allocator = null)
    {
        ArgumentNullException.ThrowIfNull(chatFactory);
        ArgumentNullException.ThrowIfNull(db);
        _chatFactory = chatFactory;
        _db = db;
        _store = store;
        _jobs = jobs;
        _options = options?.Value ?? new ISEStudioOptions();
        _allocator = allocator;
    }

    /// <summary>
    /// Resolve the current system prompt body for <see cref="PromptKey"/>
    /// according to <see cref="ISEStudioOptions.SystemLanguage"/>. Mirrors
    /// <c>TerminologyAgent.ResolveSystemPrompt</c> — the agent is Scoped so
    /// it reads <see cref="ISEStudioOptions"/> at construction time.
    /// </summary>
    public string ResolveSystemPrompt()
    {
        var lang = PromptLocales.ParseSystemLanguage(_options.SystemLanguage);
        return PromptLocales.ResolveWithFallback(PromptKey, lang)
            ?? throw new InvalidOperationException(
                $"Prompt key '{PromptKey}' is not registered in PromptLocales. " +
                "Add an entry to PromptLocales._byKey before shipping.");
    }

    /// <summary>
    /// Triage the KS's open auto-resolvable conflicts, mirroring Python
    /// <c>resolve_open_conflicts_bg(ks_id, model=None)</c>: for each open
    /// <c>duplicate</c> / <c>predicate_specialization</c> conflict, ask the
    /// LLM for a decision and attach
    /// <c>payload.recommendation = {"resolution_id", "reason", "confidence"}</c>
    /// when a valid resolution id comes back. Returns a job-log summary the
    /// caller may ignore.
    ///
    /// <para>No-ops (empty list) when the
    /// <see cref="ISEStudioOptions.AgenticConflictResolution"/> gate is off,
    /// no graph store is wired (contract-test path), an extraction is
    /// active for the KS, or no LLM provider resolves — the Python
    /// detect endpoint calls the agent only when
    /// <c>not extraction_active(session, ks.id)</c> and the agent itself
    /// early-returns when <c>settings.agentic_conflict_resolution</c> is
    /// false.</para>
    ///
    /// <para><paramref name="skipActiveExtractionGate"/> is for the
    /// extraction pipeline: Python's <c>resolve_open_conflicts_bg</c>
    /// carries no <c>extraction_active</c> gate (that guard lives in the
    /// detect endpoint only), so the job's own running row must not
    /// no-op the in-pipeline pass. The pipeline caller passes
    /// <c>true</c>; the detect endpoint keeps the default.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> TriageAsync(
        Guid ksId,
        CancellationToken ct,
        string? model = null,
        bool skipActiveExtractionGate = false)
    {
        if (!_options.AgenticConflictResolution)
        {
            return Array.Empty<string>();
        }
        if (_store is null)
        {
            return Array.Empty<string>();
        }

        if (_jobs is not null && !skipActiveExtractionGate)
        {
            var active = await _jobs.FindActiveJobAsync(ksId, ct).ConfigureAwait(false);
            if (active is not null)
            {
                return Array.Empty<string>();
            }
        }

        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null)
        {
            return Array.Empty<string>();
        }

        LlmProviderConfig providerConfig;
        try
        {
            providerConfig = await BuildProviderConfigAsync(ks, model, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No provider configured — same as an LLM hiccup: leave the
            // conflicts for a human rather than failing the detect request.
            return Array.Empty<string>();
        }

        var conflicts = await _db.Conflicts
            .Where(c => c.KnowledgeSystemId == ks.Id
                && c.Status == "open"
                && AutoTypes.Contains(c.Ctype))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var log = new List<string>();
        foreach (var conflict in conflicts)
        {
            ct.ThrowIfCancellationRequested();
            var decision = await DecideAsync(ks, conflict, providerConfig, ct).ConfigureAwait(false);
            if (decision is null)
            {
                continue;
            }
            // "skip" or an invalid id → leave for a human, untouched.
            var chosen = ConflictService.ReadResolutions(conflict)
                .FirstOrDefault(r => string.Equals(r.Id, decision.Resolution, StringComparison.Ordinal));
            if (chosen is null)
            {
                continue;
            }

            // P0 (product decision): decisions at or above the floor
            // auto-apply. Auto-apply needs the graph store and the
            // allocator (audit rows); without either (contract-test path)
            // the loop degrades to the recommendation-only behaviour.
            var canAutoApply = _store is not null && _allocator is not null;
            if (canAutoApply && decision.Confidence >= _options.AutoApplyFloor)
            {
                if (await TryAutoApplyAsync(ks, conflict, chosen, decision, ct).ConfigureAwait(false))
                {
                    log.Add($"{conflict.Title} → \"{chosen.Label}\" (auto {decision.Confidence:F2})");
                }
                // A failed apply leaves the conflict open, untouched
                // (Python logs a warning and continues — no recommendation;
                // the human triages from the unchanged row).
                continue;
            }

            // Below-floor decisions require human confirmation: attach a
            // recommendation, never apply.
            AttachRecommendation(conflict, decision);
            log.Add($"{conflict.Title} → recommend \"{chosen.Label}\" ({decision.Confidence:F2})");
        }

        if (log.Count > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return log;
    }

    // ----------------------------------------------------------------------
    // Decide loop
    // ----------------------------------------------------------------------

    /// <summary>One LLM decision. Mirrors Python <c>_decide</c>'s <c>finish</c> payload.</summary>
    private sealed record Decision(string Resolution, double Confidence, string Reason);

    /// <summary>
    /// Multi-turn ReAct loop for a single conflict. Mirrors Python
    /// <c>_decide</c>: up to <see cref="ISEStudioOptions.ConflictAgentMaxSteps"/>
    /// turns, each reply must be a single JSON object with either
    /// <c>finish</c> or <c>get_neighborhood</c> as its action. Returns
    /// <c>null</c> when the budget runs out or the LLM call fails (the
    /// conflict is left to a human).
    /// </summary>
    private async Task<Decision?> DecideAsync(
        KnowledgeSystemEntity ks,
        ConflictEntity conflict,
        LlmProviderConfig providerConfig,
        CancellationToken ct)
    {
        var resolutions = ConflictService.ReadResolutions(conflict);
        if (resolutions.Count == 0)
        {
            return null;
        }

        var ents = string.Join(", ",
            ConflictService.ReadEntities(conflict).Select(e =>
                string.IsNullOrEmpty(e.Label) ? "?" : e.Label));
        var opts = string.Join("\n", resolutions.Select(r => $"- id=\"{r.Id}\": {r.Label}"));
        var user =
            $"Conflict type: {conflict.Ctype}\n{conflict.Title}: {conflict.Detail}\n" +
            $"Entities: {ents}\n\nResolutions:\n{opts}\n\nInspect if needed, then finish.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ResolveSystemPrompt()),
            new(ChatRole.User, user),
        };

        // Python wraps openrouter.chat_sync (which resolves the client
        // per call) in the same try — a client that cannot be built is an
        // LLM hiccup, not a request failure.
        IChatClient chat;
        try
        {
            chat = _chatFactory.Create(providerConfig);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        // Python `for _ in range(settings.conflict_agent_max_steps)` — a
        // zero budget means no turns at all, so mirror it exactly.
        for (var step = 0; step < _options.ConflictAgentMaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            string reply;
            try
            {
                var response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                reply = response.Text ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // LLM hiccup → leave the conflict for a human (Python
                // catches every error from openrouter.chat_sync).
                return null;
            }

            JsonDocument data;
            try
            {
                data = JsonDocument.Parse(reply);
            }
            catch (JsonException)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, reply));
                messages.Add(new ChatMessage(ChatRole.User, "Reply with a single JSON object."));
                continue;
            }

            using (data)
            {
                var root = data.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    messages.Add(new ChatMessage(ChatRole.Assistant, reply));
                    messages.Add(new ChatMessage(ChatRole.User, "Reply with a single JSON object."));
                    continue;
                }

                var action = ReadString(root, "action");
                if (action == "finish")
                {
                    var resolution = (ReadString(root, "resolution") ?? "").Trim();
                    var confidence = ReadConfidence(root);
                    var reason = ReadString(root, "reason") ?? "";
                    if (reason.Length > 200) reason = reason[..200];
                    return new Decision(resolution, confidence, reason);
                }

                if (action == "get_neighborhood")
                {
                    var name = ReadString(root, "name") ?? "";
                    var neighborhood = Neighborhood(ks.GraphIri, name);
                    messages.Add(new ChatMessage(ChatRole.Assistant, reply));
                    messages.Add(new ChatMessage(ChatRole.User,
                        "get_neighborhood result:\n" +
                        JsonSerializer.Serialize(neighborhood, NeighborhoodSerializerOptions)));
                    continue;
                }

                messages.Add(new ChatMessage(ChatRole.Assistant, reply));
                messages.Add(new ChatMessage(ChatRole.User, "Unknown action. Use get_neighborhood or finish."));
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static double ReadConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var el))
        {
            return 0.0;
        }
        double v;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
        {
            v = d;
        }
        else if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            v = s;
        }
        else
        {
            return 0.0;
        }
        return double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
    }

    // ----------------------------------------------------------------------
    // get_neighborhood tool
    // ----------------------------------------------------------------------

    /// <summary>
    /// Structural context of a class looked up by label (case-insensitive)
    /// or IRI. Mirrors <c>retrieval.get_neighborhood(graph_iri, target)</c>:
    /// superclasses / subclasses / disjoint / equivalent / incoming and
    /// outgoing properties, built from <see cref="SchemaBuilder.BuildView"/>
    /// (the .NET equivalent of the Python <c>schema.build_view</c>).
    /// </summary>
    private Dictionary<string, object?>? Neighborhood(string graphIri, string name)
    {
        var view = SchemaBuilder.BuildView(graphIri, _store!);
        var target = name.Trim();
        var cls = view.Classes.FirstOrDefault(c =>
            string.Equals(c.Label.Trim(), target, StringComparison.OrdinalIgnoreCase)
            || c.Iri == target);
        if (cls is null)
        {
            return null;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in view.Classes) labels[c.Iri] = c.Label;
        foreach (var p in view.ObjectProperties) labels[p.Iri] = p.Label;
        foreach (var p in view.DataProperties) labels[p.Iri] = p.Label;
        string Lbl(string iri) => labels.TryGetValue(iri, out var l) ? l : iri;

        var iri = cls.Iri;
        return new Dictionary<string, object?>
        {
            ["label"] = cls.Label,
            ["comment"] = cls.Comment,
            ["superclasses"] = cls.Superclasses.Select(Lbl).ToList(),
            ["subclasses"] = view.Axioms.SubClassOf
                .Where(r => r.B == iri)
                .Select(r => Lbl(r.A))
                .ToList(),
            ["properties_out"] = view.ObjectProperties.Concat(view.DataProperties)
                .Where(p => p.Domain == iri)
                .Select(p => new Dictionary<string, object?>
                {
                    ["label"] = p.Label,
                    ["range"] = string.IsNullOrEmpty(p.RangeLabel) ? Lbl(p.Range ?? "") : p.RangeLabel,
                })
                .ToList(),
            ["properties_in"] = view.ObjectProperties
                .Where(p => p.Range == iri)
                .Select(p => new Dictionary<string, object?>
                {
                    ["label"] = p.Label,
                    ["domain"] = string.IsNullOrEmpty(p.DomainLabel) ? "" : p.DomainLabel,
                })
                .ToList(),
            ["disjoint_with"] = view.Axioms.DisjointWith
                .Where(r => r.A == iri || r.B == iri)
                .Select(r => Lbl(r.A == iri ? r.B : r.A))
                .ToList(),
            ["equivalent_class"] = view.Axioms.EquivalentClass
                .Where(r => r.A == iri || r.B == iri)
                .Select(r => Lbl(r.A == iri ? r.B : r.A))
                .ToList(),
        };
    }

    /// <summary>
    /// Python <c>json.dumps(res, ensure_ascii=False)</c> equivalent — raw
    /// Unicode so Chinese labels stay readable for the model. (The
    /// neighborhood payload never participates in a signature hash, so the
    /// relaxed encoder cannot break dedup parity.)
    /// </summary>
    private static readonly JsonSerializerOptions NeighborhoodSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ----------------------------------------------------------------------
    // Persistence helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Auto-apply one confident decision. Mirrors the auto-apply branch of
    /// Python <c>_resolve</c> (unreachable there because
    /// <c>AUTO_APPLY_TYPES</c> is empty): run the chosen editor op inside
    /// <see cref="OntologyEditor.ApplyEditAsync"/>'s own capture — opening
    /// another capture on the same graph would trip the lock-recursion
    /// guard — diff the TBox and ABox graphs around the call, flip the row
    /// to resolved, and record <c>conflict.resolve</c> audit rows (TBox row
    /// always; a grouped ABox row when the edit cascaded into instance
    /// data).
    ///
    /// <para>Returns <c>false</c> when the edit throws (the capture
    /// already reverted the RDF writes; the conflict stays open for a
    /// human). A <c>noop</c> resolution skips the editor and resolves
    /// with an empty diff, mirroring <see cref="ConflictService.ResolveAsync"/>.</para>
    /// </summary>
    private async Task<bool> TryAutoApplyAsync(
        KnowledgeSystemEntity ks,
        ConflictEntity conflict,
        ConflictDetection.Resolution chosen,
        Decision decision,
        CancellationToken ct)
    {
        var tboxIri = ks.GraphIri;
        var aboxIri = tboxIri.TrimEnd('/') + "/abox";
        var tboxGraph = new OntoNamedNode(tboxIri);
        var aboxGraph = new OntoNamedNode(aboxIri);

        var tboxPre = _store!.DumpNQuads(tboxGraph);
        var aboxPre = _store.DumpNQuads(aboxGraph);

        if (!IsNoOp(chosen.Op))
        {
            try
            {
                var editor = new OntologyEditor(_store);
                await editor.ApplyEditAsync(tboxIri, ks.BaseIri, chosen.Op, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ApplyEditAsync's capture reverted the RDF writes on the
                // throw; leave the conflict open (Python warns + continues).
                return false;
            }
        }

        var (added, removed) = StoreWrapper.DiffNQuads(tboxPre, _store.DumpNQuads(tboxGraph));
        var (aAdded, aRemoved) = StoreWrapper.DiffNQuads(aboxPre, _store.DumpNQuads(aboxGraph));
        var gid = aAdded.Length > 0 || aRemoved.Length > 0
            ? Guid.NewGuid().ToString("N")[..16]
            : null;

        // Python audit.record(action="conflict.resolve", actor_id=None,
        // actor_name="conflict-agent", detail={..., "agent": True}).
        await _allocator!.AllocateAndPersistAsync(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            ActorId = null,
            ActorName = "conflict-agent",
            Action = "conflict.resolve",
            Summary = $"Agent resolved \"{conflict.Title}\": {chosen.Label}",
            Detail = ConflictAuditDetail(conflict, decision, withReason: true),
            Graph = null,
            GroupId = gid,
            Added = added.Length == 0 ? null : added,
            Removed = removed.Length == 0 ? null : removed,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        if (aAdded.Length > 0 || aRemoved.Length > 0)
        {
            await _allocator.AllocateAndPersistAsync(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                KnowledgeSystemId = ks.Id,
                ActorId = null,
                ActorName = "conflict-agent",
                Action = "conflict.resolve",
                Summary = $"Agent resolved \"{conflict.Title}\" — cascaded to instances",
                Detail = ConflictAuditDetail(conflict, decision, withReason: false),
                Graph = aboxIri,
                GroupId = gid,
                Added = aAdded.Length == 0 ? null : aAdded,
                Removed = aRemoved.Length == 0 ? null : aRemoved,
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
        }

        conflict.Status = "resolved";
        conflict.ResolvedAt = DateTimeOffset.UtcNow;
        conflict.Resolution = decision.Resolution;
        return true;
    }

    private static bool IsNoOp(IReadOnlyDictionary<string, object?> op)
        => op.TryGetValue("op", out var v) && v is string s && s == "noop";

    private static JsonDocument ConflictAuditDetail(
        ConflictEntity conflict, Decision decision, bool withReason)
    {
        var detail = new Dictionary<string, object?>
        {
            ["conflict_id"] = conflict.LegacyId,
            ["resolution"] = decision.Resolution,
            ["confidence"] = decision.Confidence,
            ["agent"] = true,
        };
        if (withReason)
        {
            detail["reason"] = decision.Reason;
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(detail));
    }

    /// <summary>
    /// Merge <c>payload.recommendation</c> into the stored payload while
    /// preserving every existing key — Python <c>c.payload = {**c.payload,
    /// "recommendation": {...}}</c>.
    /// </summary>
    private static void AttachRecommendation(ConflictEntity conflict, Decision decision)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (conflict.Payload is { } payload && payload.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in payload.RootElement.EnumerateObject())
            {
                dict[kv.Name] = ConflictService.JsonElementToObject(kv.Value);
            }
        }
        dict["recommendation"] = new Dictionary<string, object?>
        {
            ["resolution_id"] = decision.Resolution,
            ["reason"] = decision.Reason,
            ["confidence"] = decision.Confidence,
        };
        conflict.Payload = JsonDocument.Parse(JsonSerializer.Serialize(dict));
    }

    // ----------------------------------------------------------------------
    // Provider config (mirror TerminologyAgent.BuildProviderConfigAsync)
    // ----------------------------------------------------------------------

    private async Task<LlmProviderConfig> BuildProviderConfigAsync(
        KnowledgeSystemEntity ks,
        string? model,
        CancellationToken ct)
    {
        var providerId = ks.LlmProviderId;
        if (providerId is null)
        {
            // Fall back to the singleton SystemConfig row's default provider.
            var sys = await _db.SystemConfigs.AsNoTracking()
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            providerId = sys?.LlmProviderId;
        }

        if (providerId is null)
        {
            throw new InvalidOperationException(
                "No LLM provider is configured for this knowledge system " +
                "(neither ks.LlmProviderId nor system_config.LlmProviderId is set).");
        }

        var provider = await _db.Providers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerId.Value, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"LLM provider row {providerId} referenced by knowledge system {ks.Id} was not found.");

        var resolvedModel = string.IsNullOrWhiteSpace(model) ? provider.Model : model!;
        return new LlmProviderConfig
        {
            Provider = "openai-compatible",
            ApiKey = provider.ApiKey,
            Endpoint = provider.BaseUrl,
            Model = resolvedModel,
            ConcurrencyLimit = LlmProviderConfig.ValidateConcurrencyLimit(provider.ConcurrencyLimit),
        };
    }
}
