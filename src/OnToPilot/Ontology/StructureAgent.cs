using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Llm;
using OnToPilot.Prompts;
using OntoQuad = Oxigraph.Quad;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;

namespace OnToPilot.Ontology;

/// <summary>
/// Agentic structure repair — attach isolated classes. .NET port of
/// <c>backend/app/ontology/structure_agent.py</c>.
///
/// <para>After extraction some classes end up unattached: no parent class,
/// no subclasses, and not the domain/range of any property. For each, the
/// agent asks the chat model for the single best broader parent (existing
/// class, or a new general kind when the source explicitly states the is-a
/// relation) and auto-attaches it via <c>subclass_of</c> when the
/// suggestion is confident, source-grounded, lexically safe, and not an
/// over-general catch-all. Runs at discovery time (conflict detection),
/// like the other agents; genuinely rootless classes are left alone.</para>
///
/// <para>Every LLM hiccup (unparsable reply, provider error, missing
/// provider) leaves the class untouched instead of failing the surrounding
/// request — the Python worker catches per-class and merely logs.</para>
///
/// <para>The agent is registered as a <see cref="ServiceLifetime.Scoped"/>
/// service so the dispatcher can resolve it per request and the EF
/// <see cref="OnToPilotDbContext"/> flows through naturally.</para>
/// </summary>
public sealed class StructureAgent
{
    /// <summary>
    /// Prompt registry key this agent consumes. Matches the Python
    /// backend's <c>prompt_config.register(key="tbox.structure_repair", ...)</c>
    /// entry in <c>backend/app/ontology/structure_agent.py</c>.
    /// </summary>
    public const string PromptKey = "tbox.structure_repair";

    private readonly IChatClientFactory _chatFactory;
    private readonly OnToPilotDbContext _db;
    private readonly StoreWrapper? _store;
    private readonly ExtractionJobStore? _jobs;
    private readonly LegacyIdAllocator? _allocator;
    private readonly OnToPilotOptions _options;

    public StructureAgent(
        IChatClientFactory chatFactory,
        OnToPilotDbContext db,
        StoreWrapper? store = null,
        ExtractionJobStore? jobs = null,
        LegacyIdAllocator? allocator = null,
        IOptions<OnToPilotOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatFactory);
        ArgumentNullException.ThrowIfNull(db);
        _chatFactory = chatFactory;
        _db = db;
        _store = store;
        _jobs = jobs;
        _allocator = allocator;
        _options = options?.Value ?? new OnToPilotOptions();
    }

    /// <summary>
    /// Resolve the current system prompt body for <see cref="PromptKey"/>
    /// according to <see cref="OnToPilotOptions.SystemLanguage"/>. Mirrors
    /// <c>ConflictAgent.ResolveSystemPrompt</c> — the agent is Scoped so
    /// it reads <see cref="OnToPilotOptions"/> at construction time.
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
    /// Attach the KS's isolated classes under a broader parent, mirroring
    /// Python <c>attach_isolated_bg(ks_id, model=None)</c>: propose a
    /// parent per isolated class (concurrently, capped by the provider's
    /// concurrency limit), then auto-apply the confident, verified,
    /// non-suspicious suggestions — creating the parent class first when
    /// the model proposed a new one. Returns a job-log summary the caller
    /// may ignore.
    ///
    /// <para>No-ops (empty list) when the
    /// <see cref="OnToPilotOptions.AgenticIsolatedClasses"/> gate is off,
    /// no graph store is wired (contract-test path), an extraction is
    /// active for the KS, there are no isolated classes, or no LLM
    /// provider resolves — the Python detect endpoint calls the agent only
    /// when <c>not extraction_active(session, ks.id)</c> and the agent
    /// itself early-returns when <c>settings.agentic_isolated_classes</c>
    /// is false.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> AttachIsolatedAsync(
        Guid ksId, string? model, CancellationToken ct)
    {
        if (!_options.AgenticIsolatedClasses)
        {
            return Array.Empty<string>();
        }
        if (_store is null)
        {
            return Array.Empty<string>();
        }

        if (_jobs is not null)
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

        var view = SchemaBuilder.BuildView(ks.GraphIri, _store);
        var isolated = IsolatedClasses(view);
        if (isolated.Count == 0)
        {
            return Array.Empty<string>();
        }
        var allLabels = view.Classes.Select(c => c.Label)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        // Source excerpts come from the KS's chunks, like the Python
        // worker: every chunk whose text surfaces the class label, capped
        // at 4 excerpts of 8000 chars each.
        var documentIds = await _db.Documents
            .Where(d => d.KnowledgeSystemId == ks.Id)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var chunks = documentIds.Count > 0
            ? await _db.Chunks
                .Where(c => documentIds.Contains(c.DocumentId))
                .OrderBy(c => c.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false)
            : new List<ChunkEntity>();

        string SourceFor(string label) =>
            string.Join("\n\n", chunks
                .Where(ch => RoleEvidence.SurfaceIsGrounded(ch.Text, label))
                .Take(4)
                .Select(ch => ch.Text.Length > 8000 ? ch.Text[..8000] : ch.Text));

        LlmProviderConfig providerConfig;
        try
        {
            providerConfig = await BuildProviderConfigAsync(ks, model, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No provider configured — same as an LLM hiccup: leave the
            // classes for a human rather than failing the detect request.
            return Array.Empty<string>();
        }

        // Pass 1 — propose a parent for each isolated class (no writes
        // yet). Python fans out over a ThreadPoolExecutor capped by
        // llm_concurrency(); the .NET port preserves order via Task.WhenAll.
        var workers = Math.Max(1, Math.Min(isolated.Count, providerConfig.ConcurrencyLimit ?? 1));
        using var sem = new SemaphoreSlim(workers);
        var decisions = await Task.WhenAll(isolated.Select(async c =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = allLabels.Where(l => l != c.Label).ToList();
                return await DecideAsync(
                    c.Label, existing, SourceFor(c.Label), providerConfig, ct).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        })).ConfigureAwait(false);

        var proposals = new List<(ClassView C, Proposal D)>();
        for (var i = 0; i < isolated.Count; i++)
        {
            if (decisions[i] is { } d)
            {
                proposals.Add((isolated[i], d));
            }
        }

        // A parent proposed for MANY isolated classes is almost certainly
        // an over-general catch-all (a systematic mis-guess), so don't
        // auto-attach those; leave them for a human to place.
        var parentVotes = proposals
            .Where(p => !string.IsNullOrEmpty(p.D.Parent))
            .GroupBy(p => Vocabulary.NormLabel(p.D.Parent), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var maxSameParent = _options.StructureMaxSameParent;

        // Read the TBox index ONCE and keep it in sync in-memory as new
        // parent classes get created below, instead of rescanning the whole
        // graph on every proposal (Python schema.read_index).
        var idx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in view.Classes)
        {
            idx[Vocabulary.NormLabel(c.Label)] = c.Iri;
        }

        var log = new List<string>();
        // Pass 2 — apply the confident, non-suspicious suggestions.
        foreach (var (c, d) in proposals)
        {
            ct.ThrowIfCancellationRequested();
            var parent = d.Parent;
            var conf = d.Confidence;
            if (parent.Length == 0 || conf < _options.AutoApplyFloor
                || Vocabulary.NormLabel(parent) == Vocabulary.NormLabel(c.Label))
            {
                log.Add($"{c.Label}: agent suggested \"{(parent.Length == 0 ? "skip" : parent)}\" ({conf:F2}) — left");
                continue;
            }
            if (parentVotes.TryGetValue(Vocabulary.NormLabel(parent), out var votes) && votes > maxSameParent)
            {
                // Over-general dumping ground → leave for a human.
                log.Add($"{c.Label}: \"{parent}\" proposed for {votes} classes — likely over-generalization, left");
                continue;
            }
            if (!d.Verified)
            {
                log.Add($"{c.Label}: \"{parent}\" was not verified by source evidence — left");
                continue;
            }
            if (!idx.TryGetValue(Vocabulary.NormLabel(parent), out var pIri) && !d.New)
            {
                continue; // agent named a non-existent "existing" class → don't invent it
            }
            var createdNew = pIri is null;
            var graph = new OntoNamedNode(ks.GraphIri);
            var preBytes = _store.DumpNQuads(graph);
            byte[] added = Array.Empty<byte>();
            byte[] removed = Array.Empty<byte>();
            try
            {
                // One capture per proposal, mirroring Python
                // `with store.capture(graph_iri, revert_on_error=True)`.
                // .NET revertOnError:true would ALWAYS revert (opposite
                // semantics), so we open revertOnError:false and MarkError
                // on the throw path — the ABoxService/OntologyEditor pattern.
                await using (var cap = await _store.CaptureAsync(
                    ks.GraphIri, revertOnError: false, waitTimeout: null, ct).ConfigureAwait(false))
                {
                    try
                    {
                        OntoNamedNode pNode;
                        var quads = new List<OntoQuad>();
                        if (createdNew)
                        {
                            // Python editor.apply_edit({"op": "add_class", ...}):
                            // rdf:type owl:Class + rdfs:label for a label the
                            // graph does not already hold.
                            pNode = Vocabulary.ClassNode(ks.BaseIri, parent);
                            pIri = pNode.Value;
                            quads.Add(new OntoQuad(pNode, Vocabulary.RdfType, Vocabulary.OwlClass, graph));
                            quads.Add(new OntoQuad(pNode, Vocabulary.RdfsLabel, new OntoLiteral(parent), graph));
                        }
                        else
                        {
                            pNode = new OntoNamedNode(pIri!);
                        }
                        var subNode = new OntoNamedNode(c.Iri);
                        if (subNode.Value != pNode.Value)
                        {
                            quads.Add(new OntoQuad(subNode, Vocabulary.RdfsSubClassOf, pNode, graph));
                        }
                        _store.AddQuads(graph, quads);
                    }
                    catch
                    {
                        cap.MarkError();
                        throw;
                    }
                }
                var postBytes = _store.DumpNQuads(graph);
                (added, removed) = StoreWrapper.DiffNQuads(preBytes, postBytes);
            }
            catch (Exception)
            {
                // Attach failed → the capture reverted the RDF; leave the
                // class for a human (Python logs a warning and continues).
                continue;
            }
            if (createdNew)
            {
                // Keep the in-memory index in sync so a later proposal
                // reuses this parent.
                idx[Vocabulary.NormLabel(parent)] = pIri!;
            }
            if (_allocator is not null)
            {
                await _allocator.AllocateAndPersistAsync(new AuditEventEntity
                {
                    Id = Guid.NewGuid(),
                    KnowledgeSystemId = ks.Id,
                    ActorId = null,
                    ActorName = "structure-agent",
                    Action = "tbox.attach_isolated",
                    Summary = $"Agent attached \"{c.Label}\" ⊑ \"{parent}\"{(createdNew ? " (new class)" : "")}",
                    Detail = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["class"] = c.Iri,
                        ["parent"] = parent,
                        ["new"] = createdNew,
                        ["reason"] = d.Reason,
                        ["evidence"] = d.Evidence,
                        ["confidence"] = conf,
                        ["agent"] = true,
                    })),
                    Graph = null,
                    Added = added.Length == 0 ? null : added,
                    Removed = removed.Length == 0 ? null : removed,
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct).ConfigureAwait(false);
            }
            log.Add($"{c.Label} ⊑ {parent}{(createdNew ? " (new)" : "")} (auto {conf:F2})");
        }
        return log;
    }

    // ----------------------------------------------------------------------
    // Isolated-class discovery (Python _isolated)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Classes with no parent, no children, and no property usage — the
    /// extraction step created them but never abstracted a broader kind.
    /// Mirrors Python <c>_isolated</c>.
    /// </summary>
    private static List<ClassView> IsolatedClasses(OntologyView view)
    {
        var hasChild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in view.Classes)
        {
            foreach (var s in c.Superclasses)
            {
                hasChild.Add(s);
            }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in view.ObjectProperties.Concat<PropertyView>(view.DataProperties))
        {
            foreach (var m in p.DomainMembers) used.Add(m);
            foreach (var m in p.RangeMembers) used.Add(m);
        }
        return view.Classes
            .Where(c => c.Superclasses.Count == 0
                && !hasChild.Contains(c.Iri)
                && !used.Contains(c.Iri))
            .ToList();
    }

    // ----------------------------------------------------------------------
    // Decide (Python _decide)
    // ----------------------------------------------------------------------

    /// <summary>One LLM decision. Mirrors Python <c>_decide</c>'s result dict.</summary>
    private sealed record Proposal(
        string Parent, bool New, double Confidence, string Evidence, string Reason, bool Verified);

    /// <summary>
    /// Single-shot parent suggestion for one isolated class. Mirrors Python
    /// <c>_decide</c>: the reply must be one JSON object with
    /// <c>parent</c> / <c>new</c> / <c>confidence</c> / <c>evidence</c> /
    /// <c>reason</c>. Returns <c>null</c> when there is no source text or
    /// the LLM call fails (the class is left for a human).
    /// </summary>
    private async Task<Proposal?> DecideAsync(
        string label,
        IReadOnlyList<string> existing,
        string sourceText,
        LlmProviderConfig providerConfig,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return null;
        }
        var user =
            $"Unattached class: \"{label}\"\nExisting classes: {PythonListRepr(existing)}\n\n" +
            $"SOURCE EXCERPTS:\n\"\"\"\n{sourceText}\n\"\"\"\n\nSuggest its parent.";

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

        string reply;
        try
        {
            var response = await chat.GetResponseAsync(new[]
            {
                new ChatMessage(ChatRole.System, ResolveSystemPrompt()),
                new ChatMessage(ChatRole.User, user),
            }, options: null, ct).ConfigureAwait(false);
            reply = response.Text ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // LLM hiccup → leave the class for a human (Python catches
            // every error from openrouter.chat_sync).
            return null;
        }

        JsonDocument data;
        try
        {
            data = JsonDocument.Parse(reply);
        }
        catch (JsonException)
        {
            return null;
        }
        using (data)
        {
            if (data.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var root = data.RootElement;
            var parent = ReadAsString(root, "parent").Trim();
            var isNew = ReadNewFlag(root);
            var confidence = ReadConfidence(root);
            var evidence = ReadAsString(root, "evidence").Trim();
            var reason = ReadAsString(root, "reason");
            if (reason.Length > 200) reason = reason[..200];

            var verified = false;
            if (parent.Length > 0 && confidence >= _options.AutoApplyFloor)
            {
                verified = VerifiedSourceEdge(sourceText, label, parent, evidence);
            }
            return new Proposal(parent, isNew, confidence, evidence, reason, verified);
        }
    }

    /// <summary>
    /// Groundedness + lexical safety of the proposed edge. The Python
    /// version additionally runs <c>extract._verify_tbox_candidates</c> —
    /// the LLM role-critic pipeline (<c>tbox.boundary.critic</c> /
    /// <c>tbox.boundary.adjudicator</c>) that the .NET port has not yet
    /// wired (see the gap tracker). Until then, the lexical compound-head
    /// rule from <see cref="Guard"/> stands in fail-closed.
    /// </summary>
    private static bool VerifiedSourceEdge(string sourceText, string child, string parent, string evidence)
    {
        if (!RoleEvidence.EvidenceIsGrounded(sourceText, evidence))
        {
            return false;
        }
        return Guard.IsLexicallySafeSubclass(child, parent);
    }

    /// <summary>Python <c>str(...)</c> conversion of a JSON value (e.g. <c>str(None) == "None"</c>).</summary>
    private static string ReadAsString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el))
        {
            return "";
        }
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Null => "None",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => el.GetRawText(),
        };
    }

    /// <summary>
    /// Python <c>bool(data.get("new"))</c> — a non-empty string is truthy
    /// (<c>bool("false")</c> is <c>True</c>), as is any non-zero number.
    /// </summary>
    private static bool ReadNewFlag(JsonElement root)
    {
        if (!root.TryGetProperty("new", out var el))
        {
            return false;
        }
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString()!.Length > 0,
            JsonValueKind.Number => el.TryGetDouble(out var d) && d != 0,
            _ => false,
        };
    }

    /// <summary>
    /// Python <c>float(data.get("confidence") or 0.0)</c> — accepts number
    /// or string; any parse failure falls back to 0.0.
    /// </summary>
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

    /// <summary>
    /// Python <c>str(list)</c> repr — the <c>{existing}</c> interpolation in
    /// the user prompt renders <c>['Label A', 'Label B']</c>.
    /// </summary>
    private static string PythonListRepr(IReadOnlyList<string> items) =>
        "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    // ----------------------------------------------------------------------
    // Provider config (mirror ConflictAgent.BuildProviderConfigAsync)
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
