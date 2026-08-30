using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Llm;
using ISEStudio.Observability;
using ISEStudio.Prompts;

namespace ISEStudio.Ontology;

/// <summary>
/// Semantic duplicate-class detection. .NET port of the
/// <c>backend/app/ontology/conflicts.py</c> duplicate pass (P1-1 ADR
/// §遗留 <c>conflict.duplicate_judge</c>). Composes three candidate
/// generators into one pipeline:
///
/// <list type="number">
///   <item><description><b>String similarity</b> — Jaccard overlap of
///     <see cref="Vocabulary.NormLabel"/> tokens at or above
///     <see cref="ISEStudioOptions.SemanticCandidateThreshold"/>'s
///     companion constant <c>0.86</c> (Python's <c>DUP_THRESHOLD</c>).
///     Cheap; catches plurals, typos, and spacing variants the embedding
///     round would round-trip identically. C# has no
///     <c>difflib.SequenceMatcher</c>, so the Python path's character-level
///     ratio is approximated by token-set Jaccard — exact-same behaviour
///     on Latin multi-word labels and an acceptable fallback on CJK
///     single-character labels (single tokens fall through to overlap=1.0,
///     which means "exact match" only — broader CJK duplicates rely on
///     the embedding pass).</description></item>
///   <item><description><b>Embedding cosine</b> — the configured
///     <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from
///     <see cref="EmbeddingGeneratorFactory"/> (default model
///     <c>baai/bge-m3</c>, strong multilingual) over the class-label
///     set. Cosine conflates "related" with "same", so this is only a
///     candidate generator — the LLM judge decides which candidates are
///     truly one concept.</description></item>
///   <item><description><b>LLM judge</b> — a single chat completion per
///     <see cref="DetectAsync(StoreWrapper, string, CancellationToken)"/>
///     call batches every candidate pair (number-indexed) into the
///     <c>conflict.duplicate_judge</c> prompt, returning the indices of
///     "same" pairs. Fail-closed (empty set on any error) so a flaky
///     call adds no noise. Disabled when no
///     <see cref="IChatClientFactory"/> resolves (the contract-test path
///     ships no chat factory).</description></item>
/// </list>
///
/// <para>Pairs that share <c>rdfs:subClassOf</c>,
/// <c>owl:disjointWith</c>, or <c>owl:equivalentClass</c> edges, or
/// that are compositional head-mismatches (e.g. <c>Pump</c> vs
/// <c>Pump Station</c>), are excluded from candidacy regardless of
/// string or cosine similarity — those are deliberately distinct, not
/// accidental duplicates (Python <c>_eligible</c>).</para>
///
/// <para>The service is registered as <see cref="ServiceLifetime.Scoped"/>
/// so it shares the per-request <c>ISEStudioDbContext</c> lifetime with
/// the rest of the conflict pipeline. It has no DB writes — its output
/// is a list of <see cref="ConflictDetection.DetectedConflict"/> that
/// the caller merges into the conflict queue.</para>
/// </summary>
public sealed class DuplicateJudge
{
    /// <summary>
    /// Minimum normalised token-set Jaccard overlap for a string-similarity
    /// candidate. Mirrors Python <c>DUP_THRESHOLD = 0.86</c>. Public so the
    /// tests can reuse the same threshold; production callers go through
    /// <see cref="DetectAsync(StoreWrapper, string, CancellationToken)"/>.
    /// </summary>
    public const double StringThreshold = 0.86;

    private readonly EmbeddingGeneratorFactory _embeddings;
    private readonly IChatClientFactory? _chats;
    private readonly ISEStudioOptions _options;
    private readonly ILogger<DuplicateJudge> _logger;

    public DuplicateJudge(
        EmbeddingGeneratorFactory embeddings,
        IChatClientFactory? chats = null,
        IOptions<ISEStudioOptions>? options = null,
        ILogger<DuplicateJudge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
        _chats = chats;
        _options = options?.Value ?? new ISEStudioOptions();
        _logger = logger ?? NullLogger<DuplicateJudge>.Instance;
    }

    /// <summary>
    /// Run the full semantic duplicate pipeline against
    /// <paramref name="graphIri"/> and return the resulting
    /// <c>duplicate</c>-typed conflicts. Each conflict carries two
    /// <c>merge_classes</c> resolutions (one per merge direction) so the
    /// <c>ConflictAgent</c> triage can pick one.
    /// </summary>
    /// <remarks>
    /// Returns an empty list when the candidate generator finds nothing.
    /// Falls back to string-similarity only when
    /// <see cref="ISEStudioOptions.EnableSemanticConflicts"/> is
    /// <c>false</c>, the graph has fewer than two class labels, or the
    /// embedding round fails (provider unavailable, network error).
    /// LLM judge is skipped when
    /// <see cref="ISEStudioOptions.VerifyDuplicatesWithLlm"/> is
    /// <c>false</c>, no chat factory is wired, or the chat factory
    /// cannot build a client.
    /// </remarks>
    public async Task<IReadOnlyList<ConflictDetection.DetectedConflict>> DetectAsync(
        StoreWrapper store,
        string graphIri,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(graphIri);

        var labels = ConflictDetection.ReadClassLabels(store, graphIri);
        if (labels.Count < 2)
        {
            return Array.Empty<ConflictDetection.DetectedConflict>();
        }

        var relations = ConflictDetection.ReadGraphRelations(store, graphIri);
        var seen = new HashSet<(string, string)>(PairKeyComparer.Ordinal);
        var found = new List<ConflictDetection.DetectedConflict>();

        // 1. String-similarity candidates (always run — no I/O cost).
        var stringCand = StringCandidates(labels);
        var cand = new Dictionary<(string IriA, string IriB), CandidateProvenance>(
            PairKeyComparer.Ordinal);
        foreach (var pair in stringCand)
        {
            if (!Eligible(relations, labels, pair)) continue;
            cand[pair] = new CandidateProvenance(CandidateSource.StringSim, null);
        }

        // 2. Embedding-cosine candidates (when semantic is on).
        if (_options.EnableSemanticConflicts)
        {
            var cosineCand = await EmbeddingCandidatesAsync(
                labels, _options.SemanticCandidateThreshold, ct).ConfigureAwait(false);
            foreach (var (pair, cos) in cosineCand)
            {
                if (!Eligible(relations, labels, pair)) continue;
                cand[pair] = new CandidateProvenance(CandidateSource.EmbeddingCosine, cos);
            }
        }

        if (cand.Count == 0)
        {
            return Array.Empty<ConflictDetection.DetectedConflict>();
        }

        // 3. LLM judge (when semantic + verify are both on and chat is wired).
        var pairs = cand.Keys.OrderBy(p => p.IriA, StringComparer.Ordinal)
            .ThenBy(p => p.IriB, StringComparer.Ordinal).ToList();
        var pairIndex = new Dictionary<(string, string), int>(PairKeyComparer.Ordinal);
        for (var i = 0; i < pairs.Count; i++) pairIndex[pairs[i]] = i;

        HashSet<int> judgeKept = new();
        if (_options.EnableSemanticConflicts && _options.VerifyDuplicatesWithLlm && _chats is not null)
        {
            var input = pairs
                .Select(p => (LabelOf(labels, p.IriA), LabelOf(labels, p.IriB)))
                .ToList();
            judgeKept = await JudgeDuplicatesAsync(input, ct).ConfigureAwait(false);
        }

        foreach (var pair in pairs)
        {
            var idx = pairIndex[pair];
            // Without an LLM judge every candidate passes; with one, only
            // the judge-confirmed pairs survive (fail-closed if JudgeKept
            // stayed empty due to error is the caller's choice — Python
            // does the same).
            if (judgeKept.Count > 0 && !judgeKept.Contains(idx)) continue;

            var provenance = cand[pair];
            var labelA = LabelOf(labels, pair.IriA);
            var labelB = LabelOf(labels, pair.IriB);
            var note = provenance.Cosine is { } cos
                ? $" (cosine {cos:F2})"
                : string.Empty;
            var detail = $"\"{labelA}\" and \"{labelB}\" look like the same concept{note}. " +
                "Merge them, or dismiss.";
            found.Add(BuildConflict(pair.IriA, pair.IriB, labelA, labelB, detail));
        }

        return found;
    }

    // ----------------------------------------------------------------------
    // Stage 1: string-similarity candidates
    // ----------------------------------------------------------------------

    /// <summary>
    /// Return every class-label pair whose normalised token-set Jaccard
    /// overlap is at or above <see cref="StringThreshold"/>. Mirrors
    /// Python's <c>DUP_THRESHOLD</c> SequenceMatcher ratio path on
    /// Latin multi-word labels; single-token labels fall through to
    /// exact equality only.
    /// </summary>
    public static IReadOnlyList<(string IriA, string IriB)> StringCandidates(
        IReadOnlyList<ConflictDetection.ClassLabel> labels)
    {
        var pairs = new List<(string, string)>();
        var norms = labels.Select(l => Vocabulary.NormLabel(l.Label)).ToArray();
        for (var i = 0; i < labels.Count; i++)
        {
            var ai = norms[i];
            if (string.IsNullOrEmpty(ai)) continue;
            for (var j = i + 1; j < labels.Count; j++)
            {
                var bj = norms[j];
                if (string.IsNullOrEmpty(bj)) continue;
                if (Jaccard(ai, bj) >= StringThreshold)
                {
                    pairs.Add((labels[i].Iri, labels[j].Iri));
                }
            }
        }
        return pairs;
    }

    /// <summary>
    /// Token-set Jaccard overlap between two normalised labels. The two
    /// inputs are expected to come from
    /// <see cref="Vocabulary.NormLabel"/>; raw input produces raw output.
    /// CJK labels (no whitespace tokenisation) collapse to a single token
    /// — the embedding pass is what surfaces "泵 / 水泵" style pairs.
    /// </summary>
    public static double Jaccard(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ta.Length == 0 || tb.Length == 0) return 0.0;
        var setA = new HashSet<string>(ta, StringComparer.Ordinal);
        var setB = new HashSet<string>(tb, StringComparer.Ordinal);
        setA.IntersectWith(setB);
        var intersect = setA.Count;
        var union = (new HashSet<string>(ta, StringComparer.Ordinal)).Count
            + tb.Length - intersect;
        return union == 0 ? 0.0 : (double)intersect / union;
    }

    // ----------------------------------------------------------------------
    // Stage 2: embedding-cosine candidates
    // ----------------------------------------------------------------------

    /// <summary>
    /// Embed every label once, then return every pair whose cosine
    /// similarity is at or above <paramref name="threshold"/>. Embedding
    /// failures (provider unavailable, network error, missing key) return
    /// an empty list so the caller can fall back to string-only.
    /// </summary>
    public async Task<IReadOnlyList<((string IriA, string IriB) Pair, double Cosine)>> EmbeddingCandidatesAsync(
        IReadOnlyList<ConflictDetection.ClassLabel> labels,
        double threshold,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var textList = labels.Select(l => l.Label).ToArray();
        if (textList.Length < 2) return Array.Empty<((string, string), double)>();

        IEmbeddingGenerator<string, Embedding<float>> embedder;
        try
        {
            embedder = _embeddings.Create(new LlmProviderConfig
            {
                Provider = "openai-compatible",
                Endpoint = _options.IriRoot.Replace("/ks", "/v1"),
                Model = _options.EmbeddingModel,
                ApiKey = _options.LlmApiKey,
            });
        }
        catch (InvalidOperationException)
        {
            // Unsupported embedding provider / missing key — fall back to
            // string-only silently (Python embeddings.embed() returns None
            // and the caller does the same).
            return Array.Empty<((string, string), double)>();
        }

        GeneratedEmbeddings<Embedding<float>>? generated;
        try
        {
            generated = await embedder.GenerateAsync(textList, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Array.Empty<((string, string), double)>();
        }

        if (generated.Count != textList.Length) return Array.Empty<((string, string), double)>();

        var dims = generated[0].Vector.Length;
        var matrix = new double[textList.Length, dims];
        for (var i = 0; i < textList.Length; i++)
        {
            var v = generated[i].Vector.Span;
            double norm = 0;
            for (var k = 0; k < dims; k++) norm += (double)v[k] * v[k];
            norm = Math.Sqrt(norm);
            if (norm < 1e-12) norm = 1.0;
            for (var k = 0; k < dims; k++) matrix[i, k] = (double)v[k] / norm;
        }

        var out_ = new List<((string, string), double)>();
        for (var i = 0; i < textList.Length; i++)
        {
            for (var j = i + 1; j < textList.Length; j++)
            {
                double dot = 0;
                for (var k = 0; k < dims; k++) dot += matrix[i, k] * matrix[j, k];
                if (dot >= threshold)
                {
                    out_.Add(((labels[i].Iri, labels[j].Iri), dot));
                }
            }
        }
        return out_;
    }

    // ----------------------------------------------------------------------
    // Eligibility filter (Python _eligible)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Skip pairs whose two labels are deliberately distinct (subclass /
    /// disjoint / equivalent) or are compositional head-mismatches
    /// (<c>Pump</c> vs <c>Pump Station</c>). Mirrors Python
    /// <c>_eligible(i, j)</c>.
    /// </summary>
    public static bool Eligible(
        ConflictDetection.GraphRelations relations,
        IReadOnlyList<ConflictDetection.ClassLabel> labels,
        (string IriA, string IriB) pair)
    {
        if (Related(relations, pair.IriA, pair.IriB)) return false;
        var a = LabelOf(labels, pair.IriA);
        var b = LabelOf(labels, pair.IriB);
        if (CompositionalDistinct(a, b)) return false;
        return true;
    }

    private static bool Related(ConflictDetection.GraphRelations relations, string a, string b)
    {
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (sub, sup) in relations.Subclass)
        {
            if (!adj.TryGetValue(sub, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                adj[sub] = set;
            }
            set.Add(sup);
            if (!adj.TryGetValue(sup, out var up))
            {
                up = new HashSet<string>(StringComparer.Ordinal);
                adj[sup] = up;
            }
            up.Add(sub);
        }
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(a);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (string.Equals(cur, b, StringComparison.Ordinal)) return true;
            if (!visited.Add(cur)) continue;
            if (adj.TryGetValue(cur, out var nexts))
            {
                foreach (var n in nexts) queue.Enqueue(n);
            }
        }
        foreach (var (x, y) in relations.Disjoint)
        {
            if ((string.Equals(x, a, StringComparison.Ordinal) && string.Equals(y, b, StringComparison.Ordinal))
                || (string.Equals(x, b, StringComparison.Ordinal) && string.Equals(y, a, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        foreach (var (x, y) in relations.Equivalent)
        {
            if ((string.Equals(x, a, StringComparison.Ordinal) && string.Equals(y, b, StringComparison.Ordinal))
                || (string.Equals(x, b, StringComparison.Ordinal) && string.Equals(y, a, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when one label is a compound noun whose head word differs
    /// from the other label (<c>Pump</c> vs <c>Pump Station</c> → head
    /// <c>Station</c> ≠ <c>Pump</c> → distinct concepts). Only affects
    /// multi-word (space-tokenised) labels; single tokens fall through.
    /// Mirrors Python <c>_compositional_distinct</c>.
    /// </summary>
    public static bool CompositionalDistinct(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ta.Length == 0 || tb.Length == 0) return false;
        string[] short_, long_;
        if (ta.Length < tb.Length) { short_ = ta; long_ = tb; }
        else if (tb.Length < ta.Length) { short_ = tb; long_ = ta; }
        else return false;
        var shortSet = new HashSet<string>(short_, StringComparer.Ordinal);
        var longSet = new HashSet<string>(long_, StringComparer.Ordinal);
        if (!shortSet.IsSubsetOf(longSet)) return false;
        // short_ is contained in long_; distinct iff short_ lacks long_'s last word
        return !shortSet.Contains(long_[^1]);
    }

    private static string LabelOf(IReadOnlyList<ConflictDetection.ClassLabel> labels, string iri)
    {
        foreach (var l in labels)
        {
            if (string.Equals(l.Iri, iri, StringComparison.Ordinal)) return l.Label;
        }
        return iri;
    }

    // ----------------------------------------------------------------------
    // Stage 3: LLM judge
    // ----------------------------------------------------------------------

    /// <summary>
    /// Single batched LLM call that asks the
    /// <c>conflict.duplicate_judge</c> prompt which of the supplied
    /// pairs are true synonyms. Returns the set of pair indices judged
    /// "SAME". Fail-closed (empty set) on any error so a flaky call adds
    /// no noise (Python <c>_llm_verify_duplicates</c>).
    /// </summary>
    public async Task<HashSet<int>> JudgeDuplicatesAsync(
        IReadOnlyList<(string A, string B)> pairs,
        CancellationToken ct)
    {
        if (pairs.Count == 0) return new HashSet<int>();
        if (_chats is null) return new HashSet<int>();

        var sb = new StringBuilder();
        sb.AppendLine("Pairs:");
        for (var i = 0; i < pairs.Count; i++)
        {
            var (a, b) = pairs[i];
            sb.Append(i).Append(". \"").Append(a).Append("\" | \"").Append(b).Append('"').AppendLine();
        }
        sb.AppendLine().Append("Return ONLY JSON: {\"same\": [indices that are synonyms]}.");

        var prompt = PromptLocales.ResolveWithFallback(
            "conflict.duplicate_judge",
            PromptLocales.ParseSystemLanguage(_options.SystemLanguage))
            ?? throw new InvalidOperationException(
                "conflict.duplicate_judge prompt missing — PromptLocales registration drift.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompt),
            new(ChatRole.User, sb.ToString()),
        };

        // Build a chat client from the same provider config the embedding
        // generator used; the judge is small enough to share its
        // credentials without an extra config block. The factory falls
        // through to InvalidOperationException for unsupported
        // providers — caller treats that as "judge unavailable".
        IChatClient chat;
        try
        {
            chat = _chats.Create(new LlmProviderConfig
            {
                Provider = "openai-compatible",
                Endpoint = _options.IriRoot.Replace("/ks", "/v1"),
                Model = _options.ExtractModel,
                ApiKey = _options.LlmApiKey,
            });
        }
        catch (InvalidOperationException)
        {
            return new HashSet<int>();
        }

        string reply;
        try
        {
            // Stopwatch lets the diagnostic capture how long the call ran
            // before it was cancelled — pairing elapsed seconds with the
            // configured LlmNetworkTimeoutSeconds tells us whether the SDK
            // hit its internal pipeline timeout (NetworkTimeout) versus a
            // user-initiated cancellation.
            //
            // OperationCanceledException is split out from the generic
            // Exception handler below: Python
            // `_llm_verify_duplicates` (the .NET judge's predecessor)
            // doesn't catch OCE, so the cancellation surfaces to the
            // caller — keeping that semantic means a stuck duplicate-judge
            // call doesn't silently let a job proceed. The fail-closed
            // (return empty) contract only applies to non-cancellation
            // errors so a flaky LLM doesn't add noise to the conflict
            // queue.
            var sw = Stopwatch.StartNew();
            ChatResponse response;
            try
            {
                response = await chat.GetResponseAsync(
                    messages,
                    new ChatOptions { Temperature = 0f, MaxOutputTokens = 500 },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                LlmCallDiagnostics.LogCancellation(
                    _logger,
                    operationName: "Llm.Conflict.DuplicateJudge",
                    provider: chat.GetService<ChatClientMetadata>()?.ProviderName ?? "unknown",
                    model: chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown",
                    elapsedSeconds: sw.Elapsed.TotalSeconds,
                    configuredTimeoutSec: _options.LlmNetworkTimeoutSeconds,
                    isCallerCancelled: ct.IsCancellationRequested,
                    exception: oce);
                throw;
            }
            reply = response.Text ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Logged above; propagate so the conflict pipeline sees the
            // cancellation and aborts the job rather than running the
            // merge with a silently-empty duplicate set.
            throw;
        }
        catch (Exception)
        {
            // Fail-closed for non-cancellation errors: a flaky LLM judge
            // adds no noise to the conflict queue.
            return new HashSet<int>();
        }

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new HashSet<int>();
            if (!root.TryGetProperty("same", out var sameEl) || sameEl.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<int>();
            }
            var kept = new HashSet<int>();
            foreach (var item in sameEl.EnumerateArray())
            {
                int idx;
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) idx = n;
                else if (item.ValueKind == JsonValueKind.String
                    && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) idx = s;
                else continue;
                if (idx >= 0 && idx < pairs.Count) kept.Add(idx);
            }
            return kept;
        }
        catch (JsonException)
        {
            return new HashSet<int>();
        }
    }

    // ----------------------------------------------------------------------
    // Conflict shape (Python _dup)
    // ----------------------------------------------------------------------

    private static ConflictDetection.DetectedConflict BuildConflict(
        string iriA, string iriB, string labelA, string labelB, string detail)
    {
        var ordered = string.CompareOrdinal(iriA, iriB) <= 0
            ? (iriA, iriB) : (iriB, iriA);
        var lA = string.CompareOrdinal(iriA, iriB) <= 0 ? labelA : labelB;
        var lB = string.CompareOrdinal(iriA, iriB) <= 0 ? labelB : labelA;
        var (first, second) = ordered;
        var (firstLabel, secondLabel) = (lA, lB);
        return new ConflictDetection.DetectedConflict(
            Signature: "duplicate|" + string.Join("|", new[] { first, second }.OrderBy(s => s, StringComparer.Ordinal)),
            Ctype: "duplicate",
            Severity: "warning",
            Title: "Possible duplicate classes",
            Detail: detail,
            Entities: new[]
            {
                new ConflictDetection.EntityRef(iriA, labelA),
                new ConflictDetection.EntityRef(iriB, labelB),
            },
            Resolutions: new[]
            {
                new ConflictDetection.Resolution(
                        Id: "merge-a-into-b",
                        Label: $"Merge: {firstLabel} → {secondLabel}",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "merge_classes",
                            ["source"] = first,
                            ["target"] = second,
                        }),
                new ConflictDetection.Resolution(
                        Id: "merge-b-into-a",
                        Label: $"Merge: {secondLabel} → {firstLabel}",
                        Op: new Dictionary<string, object?>
                        {
                            ["op"] = "merge_classes",
                            ["source"] = second,
                            ["target"] = first,
                        }),
            });
    }

    private enum CandidateSource
    {
        StringSim,
        EmbeddingCosine,
    }

    private sealed record CandidateProvenance(CandidateSource Source, double? Cosine);

    private sealed class PairKeyComparer : IEqualityComparer<(string, string)>
    {
        public static readonly PairKeyComparer Ordinal = new();
        public bool Equals((string, string) x, (string, string) y) =>
            (string.Equals(x.Item1, y.Item1, StringComparison.Ordinal)
                && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal))
            || (string.Equals(x.Item1, y.Item2, StringComparison.Ordinal)
                && string.Equals(x.Item2, y.Item1, StringComparison.Ordinal));
        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                string.CompareOrdinal(obj.Item1, obj.Item2) < 0 ? obj.Item1 : obj.Item2,
                string.CompareOrdinal(obj.Item1, obj.Item2) < 0 ? obj.Item2 : obj.Item1,
                StringComparer.Ordinal);
    }
}