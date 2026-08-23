using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Llm;
using OnToPilot.Observability;
using OnToPilot.Prompts;

namespace OnToPilot.Extraction;

/// <summary>
/// LLM-driven terminology proposal pass. Mirrors
/// <c>backend/app/ontology/terminology_agent.py:suggest()</c>: it asks the
/// chat model for controlled-terminology changes scoped to one
/// <see cref="KnowledgeSystemEntity"/> and one SKOS scheme, then persists
/// the result as <see cref="TermProposalEntity"/> rows with
/// <c>Status = "pending"</c>.
///
/// <para>This service is <b>advisory only</b>. It never writes to the SKOS
/// vocabulary graph &mdash; that write happens later in
/// <c>VocabularyProposalService.AcceptProposalAsync</c> when a human
/// resolves the pending row. The same Python backend invariant holds here:
/// the agent is the suggester, the reviewer is the writer.</para>
///
/// <para>The agent is registered as a <see cref="ServiceLifetime.Scoped"/>
/// service so the orchestrator / dispatcher can resolve it per request and
/// the EF <see cref="OnToPilotDbContext"/> flows through naturally.</para>
///
/// <para>The system prompt is resolved at call time from
/// <see cref="PromptLocales"/> against <see cref="OnToPilotOptions.SystemLanguage"/>.
/// The Python parity key is <c>terminology.steward</c>; the older
/// <c>terminology.propose</c> name was a stale artefact from the first
/// .NET slice and has been renamed so the .NET and Python registries line
/// up byte-for-byte.</para>
/// </summary>
public sealed class TerminologyAgent
{
    /// <summary>
    /// Prompt registry key the orchestration snapshot references. Matches
    /// the Python backend's <c>prompt_config</c> registry entry of the
    /// same name (see <c>backend/app/prompt_locales.py</c>); the prior
    /// <c>terminology.propose</c> name was an early .NET-only artefact.
    /// </summary>
    public const string PromptKey = "terminology.steward";

    /// <summary>
    /// Resolve the current system prompt body for <see cref="PromptKey"/>
    /// according to <see cref="OnToPilotOptions.SystemLanguage"/>. See
    /// <c>TBoxExtractionService.ResolveSystemPrompt</c> for the rationale;
    /// the agent is Scoped so it reads <see cref="OnToPilotOptions"/>
    /// through <see cref="IOptions{TOptions}"/> once at construction time.
    /// </summary>
    public string ResolveSystemPrompt()
    {
        var lang = PromptLocales.ParseSystemLanguage(_options.SystemLanguage);
        return PromptLocales.ResolveWithFallback(PromptKey, lang)
            ?? throw new InvalidOperationException(
                $"Prompt key '{PromptKey}' is not registered in PromptLocales. " +
                "Add an entry to PromptLocales._byKey before shipping.");
    }

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        WriteIndented = false,
        // UTF-8 vs \uXXXX escape parity (Round 2 finding).
        //
        // Python's `json.dumps(..., ensure_ascii=True)` (the default)
        // escapes every non-ASCII byte to a `\uXXXX` sequence before
        // hashing in `_signature()`. System.Text.Json's default encoder
        // is `JavaScriptEncoder.Default`, which already escapes
        // non-ASCII to `\uXXXX` — but we set it explicitly so a future
        // contributor who switches the encoder to one of the `Unsafe*`
        // variants (which emit raw UTF-8 bytes) doesn't silently break
        // signature parity for Chinese, accented, Cyrillic, or other
        // non-English terminology.
        //
        // Separator parity (Round 1 finding).
        //
        // Match Python `json.dumps(..., separators=(",", ":"))` so the
        // signature bytes line up with `terminology_agent._signature()`.
        // System.Text.Json has no built-in way to change the ": " / ", "
        // separators — it always emits a single space after a key/value
        // separator and after each array/property separator when
        // WriteIndented is false — so we post-process the byte stream via
        // <see cref="SerializeCompactBytes"/> to drop the whitespace.
        Encoder = JavaScriptEncoder.Default,
    };

    private readonly IChatClientFactory _chatFactory;
    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;
    private readonly OnToPilotOptions _options;

    public TerminologyAgent(
        IChatClientFactory chatFactory,
        OnToPilotDbContext db,
        LegacyIdAllocator allocator,
        IOptions<OnToPilotOptions> options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(chatFactory);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);
        _chatFactory = chatFactory;
        _db = db;
        _allocator = allocator;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Run an LLM-driven propose pass. Loads the cited chunks, sends them
    /// (plus scheme context + the steward system prompt) to the configured
    /// chat client, parses the JSON <c>{"proposals": [...]}</c> reply, and
    /// inserts one <see cref="TermProposalEntity"/> row per accepted
    /// proposal with <c>Status = "pending"</c>. Does NOT touch the RDF
    /// graph &mdash; the SKOS write happens later at
    /// <c>AcceptProposal</c> time.
    /// </summary>
    /// <param name="ks">Owning knowledge system.</param>
    /// <param name="schemeIri">
    /// SKOS scheme IRI the proposals should be scoped to. Recorded in the
    /// payload so the reviewer can tell which scheme the agent considered.
    /// </param>
    /// <param name="chunkIds">
    /// <see cref="ChunkEntity.LegacyId"/> values the LLM should consider as
    /// source excerpts. Mirrors the Python <c>chunks[:max_chunks]</c>
    /// trimming &mdash; the caller is expected to have already capped the
    /// list to the configured maximum.
    /// </param>
    /// <param name="model">
    /// Optional model override. When <c>null</c>, the LLM provider row's
    /// <c>Model</c> column is used. Empty string falls back too (treated
    /// as <c>null</c>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly inserted <see cref="TermProposalEntity"/> rows.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no LLM provider is configured for the knowledge system
    /// (or the system default).
    /// </exception>
    public async Task<IReadOnlyList<TermProposalEntity>> SuggestAsync(
        KnowledgeSystemEntity ks,
        string schemeIri,
        IReadOnlyList<long> chunkIds,
        string? model,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(schemeIri);
        ArgumentNullException.ThrowIfNull(chunkIds);
        ct.ThrowIfCancellationRequested();

        if (chunkIds.Count == 0)
        {
            return Array.Empty<TermProposalEntity>();
        }

        var chunks = await LoadChunksAsync(ks, chunkIds, ct).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            return Array.Empty<TermProposalEntity>();
        }

        var providerConfig = await BuildProviderConfigAsync(ks, model, ct).ConfigureAwait(false);
        var chat = _chatFactory.Create(providerConfig);

        var provider = ResolveProvider(chat);
        var resolvedModel = ResolveModel(chat);

        var proposals = await Telemetry.LlmSource.WithLlmActivity(
            operationName: "Llm.TermSuggest",
            provider: provider,
            model: resolvedModel,
            action: async innerCt =>
            {
                var messages = BuildMessages(ks, schemeIri, chunks);
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(messages, options: null, innerCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Transient provider/network failure: do not abort the
                    // whole dispatch. Return an empty proposal set so the
                    // dispatcher can still surface a (smaller) result.
                    return Array.Empty<JsonElement>();
                }

                return ParseProposals(response.Text);
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (proposals.Count == 0)
        {
            return Array.Empty<TermProposalEntity>();
        }

        var allowedChunkIds = new HashSet<long>(chunks.Keys);
        var pending = new List<TermProposalEntity>(proposals.Count);
        var now = _clock.GetUtcNow();
        foreach (var raw in proposals)
        {
            ct.ThrowIfCancellationRequested();
            var row = TryBuildProposal(raw, ks, schemeIri, allowedChunkIds, chunks, now);
            if (row is not null)
            {
                pending.Add(row);
            }
        }

        if (pending.Count == 0)
        {
            return Array.Empty<TermProposalEntity>();
        }

        // Signature dedup: Python `terminology_agent.suggest()` queries
        // `TermProposal` for an existing row with the same (ks_id,
        // signature) tuple and skips when one is found, so re-running the
        // agent on the same chunks does not pile up duplicate rows. We
        // collect all candidate signatures up front and batch the lookup
        // into one round trip — the per-row FirstOrDefaultAsync the brief
        // suggested is O(N) round trips and unnecessary at typical
        // proposal sizes (<=50 per call).
        var signatures = pending.Select(r => r.Signature).Distinct().ToList();
        var existingSignatures = await QueryExistingSignaturesAsync(ks, signatures, ct).ConfigureAwait(false);

        var rows = new List<TermProposalEntity>(pending.Count);
        // Filter duplicates FIRST so the allocator reserves exactly the
        // range it will persist. Per-row NextAsync would return the same
        // id for every iteration because SELECT MAX runs in autocommit
        // and doesn't see rows queued for SaveChanges — the original
        // pre-refactor code worked by computing MAX once and incrementing
        // in memory. AllocateManyAndPersistAsync preserves that semantic
        // (one MAX read + contiguous range reserved + persisted in one
        // transaction under the per-table advisory lock) without the
        // pre-refactor race where two concurrent batches could both read
        // the same MAX and collide on UNIQUE(legacy_id).
        foreach (var row in pending)
        {
            if (existingSignatures.Contains(row.Signature))
            {
                continue;
            }
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            return Array.Empty<TermProposalEntity>();
        }

        await _allocator.AllocateManyAndPersistAsync(rows, ct).ConfigureAwait(false);
        return rows;
    }

    private async Task<HashSet<string>> QueryExistingSignaturesAsync(
        KnowledgeSystemEntity ks,
        IReadOnlyList<string> signatures,
        CancellationToken ct)
    {
        if (signatures.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var existing = await _db.TermProposals
            .AsNoTracking()
            .Where(p => p.KnowledgeSystemId == ks.Id && signatures.Contains(p.Signature))
            .Select(p => p.Signature)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new HashSet<string>(existing, StringComparer.Ordinal);
    }

    // ----------------------------------------------------------------------
    // Chunk loading
    // ----------------------------------------------------------------------

    private async Task<Dictionary<long, ChunkEntity>> LoadChunksAsync(
        KnowledgeSystemEntity ks,
        IReadOnlyList<long> chunkIds,
        CancellationToken ct)
    {
        // Order by Idx within each document to keep chunk ordering stable
        // across calls; chunkId is the Python-era integer id, so the
        // dictionary key is LegacyId. The chunk rows are linked to
        // DocumentEntity, so we join to filter on the parent document's
        // knowledge system — the brief requested `db.Chunks.Where(c =>
        // chunkIds.Contains(c.Id))`; LegacyId is the wire-format id the
        // Python backend uses for chunks.
        var rows = await _db.Chunks
            .Join(_db.Documents,
                c => c.DocumentId,
                d => d.Id,
                (c, d) => new { Chunk = c, Document = d })
            .Where(join => join.Document.KnowledgeSystemId == ks.Id
                && chunkIds.Contains(join.Chunk.LegacyId))
            .OrderBy(join => join.Chunk.DocumentId).ThenBy(join => join.Chunk.Idx)
            .Select(join => join.Chunk)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ToDictionary(c => c.LegacyId);
    }

    // ----------------------------------------------------------------------
    // Provider config (resolve ProviderEntity → LlmProviderConfig)
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

        // The chat factory dispatches by Provider name; the ProviderEntity
        // table only stores a BaseUrl (treated as an OpenAI-compatible HTTP
        // endpoint) plus credentials. Routing through "openai-compatible"
        // is the safest default — it works for every endpoint that exposes
        // an OpenAI-shaped /v1/chat/completions surface.
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

    // ----------------------------------------------------------------------
    // Message construction
    // ----------------------------------------------------------------------

    private List<ChatMessage> BuildMessages(
        KnowledgeSystemEntity ks,
        string schemeIri,
        IReadOnlyDictionary<long, ChunkEntity> chunks)
    {
        var sourceBlocks = new List<string>(chunks.Count);
        foreach (var (chunkId, chunk) in chunks)
        {
            var excerpt = chunk.Text.Length > 2000 ? chunk.Text[..2000] : chunk.Text;
            sourceBlocks.Add($"[chunk:{chunkId}]\n{excerpt}");
        }

        var prompt =
            "CURRENT CONTROLLED TERMS SCHEME:\n" + schemeIri +
            "\n\nSOURCE EXCERPTS:\n" + string.Join("\n\n", sourceBlocks) +
            "\n\nPropose controlled-terminology changes.";

        return new List<ChatMessage>
        {
            new(ChatRole.System, ResolveSystemPrompt()),
            new(ChatRole.User, prompt),
        };
    }

    // ----------------------------------------------------------------------
    // Reply parsing
    // ----------------------------------------------------------------------

    private static IReadOnlyList<JsonElement> ParseProposals(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return Array.Empty<JsonElement>();
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            JsonElement proposals;
            if (root.ValueKind == JsonValueKind.Array)
            {
                proposals = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("proposals", out var nested)
                && nested.ValueKind == JsonValueKind.Array)
            {
                proposals = nested;
            }
            else
            {
                return Array.Empty<JsonElement>();
            }

            var list = new List<JsonElement>(proposals.GetArrayLength());
            foreach (var entry in proposals.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    list.Add(entry.Clone());
                }
            }
            return list;
        }
        catch (JsonException)
        {
            // Malformed LLM reply — treat as zero proposals rather than
            // aborting the whole suggest pass.
            return Array.Empty<JsonElement>();
        }
    }

    // ----------------------------------------------------------------------
    // Per-proposal row construction
    // ----------------------------------------------------------------------

    private static TermProposalEntity? TryBuildProposal(
        JsonElement raw,
        KnowledgeSystemEntity ks,
        string schemeIri,
        HashSet<long> allowedChunkIds,
        IReadOnlyDictionary<long, ChunkEntity> chunks,
        DateTimeOffset now)
    {
        var action = ResolveAction(raw);
        var sourceIds = ResolveSourceChunkIds(raw, allowedChunkIds);
        if (sourceIds.Count == 0)
        {
            // Every proposal needs at least one cited chunk so the
            // reviewer can find the evidence. Drop silently — matches the
            // Python `continue` when no source ids remain.
            return null;
        }

        var term = ResolveTerm(raw, action);
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        // _source_contains grounding check (parity with Python
        // `terminology_agent._filter_to_supported_labels` after the
        // `_sanitize` step). The term must appear as a substring in
        // at least one cited chunk's text — otherwise the LLM is
        // hallucinating a term that has no evidence in the source
        // corpus, and the reviewer would have nothing to anchor on.
        // Case-insensitive + whitespace-trimmed to match what a human
        // reviewer would call "the term appears here". Drop silently
        // (Python `continue` semantics) — the agent's caller already
        // counts accepted rows; rejected ones don't contribute.
        if (!IsTermGroundedInChunks(term!, sourceIds, chunks))
        {
            return null;
        }

        var targetIri = ResolveTargetIri(raw);
        var language = ResolveLanguage(raw);
        var description = ResolveDescription(raw, action);
        var aliases = ResolveAliases(raw, action, language, term);
        var confidence = ClampConfidence(raw);
        var reason = ResolveReason(raw);

        var payload = BuildPayload(
            action: action,
            schemeIri: schemeIri,
            language: language,
            term: term!,
            aliases: aliases,
            description: description,
            broaderIri: ResolveBroader(raw),
            mappedIri: ResolveMapped(raw));

        var evidence = BuildEvidence(sourceIds, chunks);
        var sourceIdsJson = JsonSerializer.SerializeToUtf8Bytes(sourceIds, PayloadSerializerOptions);
        var evidenceJson = JsonSerializer.SerializeToUtf8Bytes(evidence, PayloadSerializerOptions);
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadSerializerOptions);

        var signature = ComputeSignature(action, targetIri, payload);

        return new TermProposalEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            Signature = signature,
            Action = action,
            Term = term!,
            TargetIri = targetIri,
            Status = "pending",
            Payload = JsonDocument.Parse(payloadJson),
            Confidence = confidence,
            Reason = reason,
            Evidence = JsonDocument.Parse(evidenceJson),
            SourceChunkIds = JsonDocument.Parse(sourceIdsJson),
            ExtractionJobId = null,
            ProposedBy = "terminology-agent",
            CreatedAt = now,
        };
    }

    private static string ResolveAction(JsonElement raw)
    {
        if (raw.TryGetProperty("action", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (string.Equals(s, "create", StringComparison.OrdinalIgnoreCase)) return "create";
            if (string.Equals(s, "add_alias", StringComparison.OrdinalIgnoreCase)) return "add_alias";
            if (string.Equals(s, "update", StringComparison.OrdinalIgnoreCase)) return "update";
        }
        return "create";
    }

    private static string? ResolveTargetIri(JsonElement raw)
    {
        if (raw.TryGetProperty("target_concept_iri", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static string? ResolveTerm(JsonElement raw, string action)
    {
        // For "create": preferred_label is the canonical term.
        if (raw.TryGetProperty("preferred_label", out var pref) && pref.ValueKind == JsonValueKind.String)
        {
            var s = pref.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
        }

        // For "add_alias": the first alternate label carries the term on the row.
        if (action == "add_alias"
            && raw.TryGetProperty("alternate_labels", out var al)
            && al.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in al.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
                }
            }
        }

        // For "update" / fallback: target_concept_iri is recorded as the term
        // (matches the Python `concept_by_iri[target]["display_label"]`).
        var target = ResolveTargetIri(raw);
        if (!string.IsNullOrEmpty(target)) return target;

        return null;
    }

    private static string ResolveLanguage(JsonElement raw)
    {
        if (raw.TryGetProperty("language", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
        }
        return "en";
    }

    private static string ResolveDescription(JsonElement raw, string action)
    {
        if (action != "create") return string.Empty;
        if (raw.TryGetProperty("description", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                // Match the Python `[str(raw.get("description", "")).strip()[:1000]]` cap.
                return s!.Trim().Length > 1000 ? s!.Trim()[..1000] : s!.Trim();
            }
        }
        return string.Empty;
    }

    private static List<string> ResolveAliases(JsonElement raw, string action, string language, string? term)
    {
        var result = new List<string>();
        if (!raw.TryGetProperty("alternate_labels", out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            var trimmed = s!.Trim();
            if (action == "create" && string.Equals(trimmed, term, StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(trimmed);
        }
        return result;
    }

    private static string? ResolveBroader(JsonElement raw)
    {
        if (raw.TryGetProperty("broader_concept_iri", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }
        return null;
    }

    private static string? ResolveMapped(JsonElement raw)
    {
        if (raw.TryGetProperty("mapped_entity_iri", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }
        return null;
    }

    private static List<long> ResolveSourceChunkIds(JsonElement raw, HashSet<long> allowedChunkIds)
    {
        var result = new List<long>();
        if (!raw.TryGetProperty("source_chunk_ids", out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in prop.EnumerateArray())
        {
            long parsedLong;
            try
            {
                if (item.ValueKind == JsonValueKind.Number)
                {
                    parsedLong = item.GetInt64();
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    parsedLong = long.Parse(item.GetString() ?? string.Empty, CultureInfo.InvariantCulture);
                }
                else
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                continue;
            }

            if (allowedChunkIds.Contains(parsedLong) && !result.Contains(parsedLong))
            {
                result.Add(parsedLong);
            }
        }
        return result;
    }

    /// <summary>
    /// _source_contains grounding check. Returns <c>true</c> when
    /// <paramref name="term"/> (case-insensitive, whitespace-trimmed)
    /// appears as a substring in the text of at least one cited chunk.
    /// Mirrors Python's
    /// <c>terminology_agent._filter_to_supported_labels</c>: the term
    /// must be supported by the source corpus, otherwise the LLM is
    /// hallucinating and the reviewer has no evidence to anchor on.
    /// </summary>
    /// <remarks>
    /// Substring matching (not word-boundary) is intentional: many
    /// legitimate terms are multi-word ("centrifugal pump") and the
    /// reviewer's mental model is "this term string is present in this
    /// chunk" rather than "this single token matches". Empty / null
    /// chunk text is treated as "no evidence" so the proposal is
    /// rejected — same as Python's <c>continue</c> when the chunk
    /// lookup misses.
    /// </remarks>
    private static bool IsTermGroundedInChunks(
        string term,
        List<long> sourceIds,
        IReadOnlyDictionary<long, ChunkEntity> chunks)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        var needle = term.Trim();

        foreach (var id in sourceIds)
        {
            if (!chunks.TryGetValue(id, out var chunk) || chunk is null) continue;
            if (string.IsNullOrEmpty(chunk.Text)) continue;
            // OrdinalIgnoreCase — keeps the check culture-stable so the
            // test fixture "Pump" matches corpus text "pump" without a
            // Turkish-I surprise.
            if (chunk.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static double? ClampConfidence(JsonElement raw)
    {
        if (!raw.TryGetProperty("confidence", out var prop)) return null;
        double v;
        try
        {
            v = prop.ValueKind switch
            {
                JsonValueKind.Number => prop.GetDouble(),
                JsonValueKind.String => double.Parse(prop.GetString() ?? string.Empty, CultureInfo.InvariantCulture),
                _ => double.NaN,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return null;
        }
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        if (v < 0.0) return 0.0;
        if (v > 1.0) return 1.0;
        return v;
    }

    private static string? ResolveReason(JsonElement raw)
    {
        if (!raw.TryGetProperty("reason", out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var s = prop.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        var trimmed = s!.Trim();
        // Match the Python `[:500]` cap.
        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

    private static Dictionary<string, object?> BuildPayload(
        string action,
        string schemeIri,
        string language,
        string term,
        List<string> aliases,
        string description,
        string? broaderIri,
        string? mappedIri)
    {
        // Mirrors the sanitized "create" payload Python writes at
        // `terminology_agent.py:184-196` — every key the Python backend
        // emits must round-trip byte-for-byte so the dedup query and the
        // signature match. AcceptProposal reads these fields back verbatim.
        //
        // Nested objects are emitted as Dictionary<string, object?>
        // (rather than anonymous types) so <see cref="SortKeysDeep"/> can
        // recursively reorder their keys at signature-hash time.
        //
        // Key-order note: `_signature()` calls `json.dumps(..., sort_keys=True)`
        // so the signature hash is independent of payload key order. The
        // stored Payload column preserves whatever order Dictionary gives
        // (insertion order); the reviewer-facing fields are stable.
        return new Dictionary<string, object?>
        {
            ["scheme_iri"] = schemeIri,
            ["action"] = action,
            ["language"] = language,
            ["pref_labels"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["value"] = term,
                    ["language"] = language,
                },
            },
            ["alt_labels"] = aliases.ConvertAll(a => new Dictionary<string, object?>
            {
                ["value"] = a,
                ["language"] = language,
            }),
            ["hidden_labels"] = Array.Empty<object>(),
            ["description"] = description,
            ["notation"] = string.Empty,
            ["broader"] = string.IsNullOrEmpty(broaderIri) ? Array.Empty<string>() : new[] { broaderIri },
            ["related"] = Array.Empty<object>(),
            ["mapped_entity_iri"] = mappedIri,
            ["status"] = "active",
            ["origin"] = "agent",
        };
    }

    private static List<Dictionary<string, object?>> BuildEvidence(
        List<long> sourceIds,
        IReadOnlyDictionary<long, ChunkEntity> chunks)
    {
        var evidence = new List<Dictionary<string, object?>>(sourceIds.Count);
        foreach (var id in sourceIds)
        {
            if (!chunks.TryGetValue(id, out var chunk)) continue;
            var snippet = chunk.Text.Length > 600 ? chunk.Text[..600] : chunk.Text;
            evidence.Add(new Dictionary<string, object?>
            {
                ["chunk_id"] = id,
                ["document_id"] = chunk.DocumentId,
                ["snippet"] = snippet.Trim(),
            });
        }
        return evidence;
    }

    /// <summary>
    /// Internal so parity tests in <c>OnToPilot.Tests</c> can lock the
    /// Python-equivalent signature for known payloads (Round 2 finding).
    /// </summary>
    internal static string ComputeSignature(string action, string? targetIri, Dictionary<string, object?> payload)
    {
        // Mirrors `terminology_agent._signature()`: a sorted, compact JSON of
        // (action, target_iri, payload) hashed with SHA-256. Two proposals
        // that sanitise to the same (action, target, payload) collapse to
        // one signature so the dedup query in the next pass catches them.
        //
        // Round 2 finding: Python's `json.dumps(..., sort_keys=True)`
        // sorts at every depth, not just the top level. C#'s default
        // Dictionary preserves insertion order, so a payload built with
        // anonymous types (or a Dictionary that was inserted into out of
        // order) would produce a different byte stream from Python's.
        // <see cref="SortKeysDeep"/> walks the entire graph and converts
        // every nested IDictionary to a SortedDictionary so the byte
        // stream matches Python for any payload shape.
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["payload"] = SortKeysDeep(payload),
            ["target_iri"] = targetIri,
        };
        // Compact JSON (`separators=(",", ":")` in Python) is required so
        // the C# signature bytes line up with the Python backend. See
        // <see cref="SerializeCompactBytes"/> for the post-process.
        var bytes = SerializeCompactBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Recursively rewrite every <see cref="IDictionary{TKey, TValue}"/>
    /// in the supplied graph as a <see cref="SortedDictionary{TKey, TValue}"/>
    /// so the byte stream System.Text.Json emits matches Python's
    /// <c>sort_keys=True</c>. Arrays are recursed into but their element
    /// order is preserved (Python does not sort array elements either).
    /// </summary>
    private static object? SortKeysDeep(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary<string, object?> dict:
            {
                var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kvp in dict)
                {
                    sorted[kvp.Key] = SortKeysDeep(kvp.Value);
                }
                return sorted;
            }
            case System.Collections.IList list when value is not string:
            {
                var rebuilt = new List<object?>(list.Count);
                foreach (var item in list)
                {
                    rebuilt.Add(SortKeysDeep(item));
                }
                return rebuilt;
            }
            default:
                return value;
        }
    }

    /// <summary>
    /// Serialize an object to UTF-8 JSON with Python's compact separators
    /// (<c>json.dumps(..., separators=(",", ":"))</c>). System.Text.Json has
    /// no API to drop the ": " / ", " whitespace it always emits with
    /// <c>WriteIndented = false</c>, so we walk the resulting bytes and
    /// strip whitespace that falls outside a JSON string literal. The
    /// walker handles backslash escapes correctly so an escaped quote
    /// inside a string does not flip the <c>inString</c> flag.
    ///
    /// <para>The walker additionally lowercases any <c>\uXXXX</c> escape
    /// sequence it finds outside a string literal. System.Text.Json's
    /// <see cref="JavaScriptEncoder.Default"/> emits uppercase hex digits
    /// (<c>术</c>) while Python's <c>ensure_ascii=True</c> uses
    /// lowercase (<c>术</c>). Without this fix, the SHA-256 hashes
    /// for the same logical payload diverge between the two runtimes and
    /// signature-based dedup silently breaks for non-ASCII terminology
    /// (Chinese, accented Latin-1, Cyrillic, …).</para>
    /// </summary>
    private static byte[] SerializeCompactBytes(object value)
    {
        var defaultBytes = JsonSerializer.SerializeToUtf8Bytes(value, PayloadSerializerOptions);
        if (defaultBytes.Length == 0) return defaultBytes;

        // Pass 1 — strip the ": " / ", " whitespace System.Text.Json
        // emits with WriteIndented = false. Walker tracks string-vs-
        // non-string so we only drop whitespace outside a JSON string
        // literal (whitespace inside a string is meaningful data).
        var compacted = new byte[defaultBytes.Length];
        var written = 0;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < defaultBytes.Length; i++)
        {
            var b = defaultBytes[i];
            if (escaped)
            {
                compacted[written++] = b;
                escaped = false;
                continue;
            }
            if (inString)
            {
                if (b == (byte)'\\')
                {
                    compacted[written++] = b;
                    escaped = true;
                    continue;
                }
                if (b == (byte)'"')
                {
                    inString = false;
                }
                compacted[written++] = b;
                continue;
            }
            if (b == (byte)'"' || b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r')
            {
                if (b == (byte)'"')
                {
                    inString = true;
                }
                else
                {
                    continue;
                }
            }
            compacted[written++] = b;
        }
        var compactedSlice = written == defaultBytes.Length ? compacted : compacted[..written];

        // Pass 2 — lowercase any \uXXXX escape sequence anywhere in the
        // compacted byte stream (Round 2 finding). System.Text.Json's
        // JavaScriptEncoder.Default emits uppercase hex digits (术
        // → 术); Python's ensure_ascii=True uses lowercase
        // (术). Doing the conversion in a separate pass keeps the
        // inString tracking simple — we only have to recognise the
        // exact \uXXXX pattern, not also the surrounding context.
        for (var i = 0; i + 5 < compactedSlice.Length; i++)
        {
            if (compactedSlice[i] != (byte)'\\' || compactedSlice[i + 1] != (byte)'u')
            {
                continue;
            }
            if (!IsAsciiHexDigit(compactedSlice[i + 2])
                || !IsAsciiHexDigit(compactedSlice[i + 3])
                || !IsAsciiHexDigit(compactedSlice[i + 4])
                || !IsAsciiHexDigit(compactedSlice[i + 5]))
            {
                continue;
            }
            for (var k = 2; k <= 5; k++)
            {
                var digit = compactedSlice[i + k];
                if (digit >= (byte)'A' && digit <= (byte)'F')
                {
                    compactedSlice[i + k] = (byte)(digit + 32);
                }
            }
            i += 5;
        }
        return compactedSlice;
    }

    private static bool IsAsciiHexDigit(byte c) =>
        (c >= (byte)'0' && c <= (byte)'9')
        || (c >= (byte)'A' && c <= (byte)'F')
        || (c >= (byte)'a' && c <= (byte)'f');

    // ----------------------------------------------------------------------
    // Chat-client metadata helpers (mirror TBoxExtractionService)
    // ----------------------------------------------------------------------

    private static string ResolveProvider(IChatClient chat)
    {
        var metadata = chat.GetService<ChatClientMetadata>();
        return metadata?.ProviderName ?? "unknown";
    }

    private static string ResolveModel(IChatClient chat)
    {
        var metadata = chat.GetService<ChatClientMetadata>();
        return metadata?.DefaultModelId ?? "unknown";
    }
}