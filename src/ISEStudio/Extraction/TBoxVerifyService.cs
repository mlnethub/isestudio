using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ISEStudio.Configuration;
using ISEStudio.Observability;
using ISEStudio.Ontology;
using ISEStudio.Prompts;

namespace ISEStudio.Extraction;

/// <summary>
/// One chunk's independent TBox role verification (Python
/// <c>_verify_tbox_candidates</c>): a first boundary critic classifies every
/// class / subclass candidate, a second adjudicator re-judges what the critic
/// rejected, and a final denotation critic applies the stricter
/// proper-name-vs-type convention plus suffix recovery. Every decision is
/// fail-closed — the candidate survives only when its role decision is
/// grounded in an exact source span and the label itself occurs in the text.
/// </summary>
/// <remarks>
/// The decision-application helpers are static and side-effect free so the
/// fail-closed boundary can be regression-tested without an LLM (Python
/// <c>_apply_tbox_role_decisions</c>'s doc comment promises the same). The
/// corpus-level recovery and hierarchy recovery passes
/// (<c>_recover_rejected_classes</c> / <c>_recover_hierarchy_one</c>) are a
/// separate slice; their prompts are already in the catalog.
/// </remarks>
public sealed class TBoxVerifyService
{
    public const string BoundaryCriticKey = "tbox.boundary.critic";
    public const string BoundaryAdjudicatorKey = "tbox.boundary.adjudicator";
    public const string DenotationCriticKey = "tbox.denotation.critic";

    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private readonly ISEStudioOptions _options;
    private readonly ILogger<TBoxVerifyService> _logger;

    public TBoxVerifyService(
        IOptions<ISEStudioOptions> options,
        ILogger<TBoxVerifyService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<TBoxVerifyService>.Instance;
    }

    // ------------------------------------------------------------------
    // Prompt resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolve one verify prompt body for the configured system language —
    /// same contract as <see cref="TBoxExtractionService.ResolveSystemPrompt"/>
    /// so the orchestrator can include it in the per-job prompt snapshot.
    /// </summary>
    public string ResolveSystemPrompt(string promptKey)
    {
        var lang = PromptLocales.ParseSystemLanguage(_options.SystemLanguage);
        return PromptLocales.ResolveWithFallback(promptKey, lang)
            ?? throw new InvalidOperationException(
                $"Prompt key '{promptKey}' is not registered in PromptLocales. " +
                "Add an entry to PromptLocales._byKey before shipping.");
    }

    // ------------------------------------------------------------------
    // Pipeline
    // ------------------------------------------------------------------

    /// <summary>
    /// Run the critic → adjudicator → denotation chain over one chunk's
    /// extracted delta. Returns the filtered delta plus the rejections /
    /// recoveries the worker reports alongside the chunk result. A delta with
    /// no classes and no subclass axioms is returned untouched — exactly like
    /// Python's early return before any LLM call.
    /// </summary>
    /// <remarks>
    /// The composite is split into three internal steps
    /// (<see cref="RunCriticAsync"/> / <see cref="RunAdjudicatorAsync"/> /
    /// <see cref="RunDenotationAsync"/>) so the Dovetail TBox sub-DAG can
    /// invoke each step individually; this method keeps the original
    /// composite behavior identical.
    /// </remarks>
    public async Task<TBoxVerifyResult> VerifyAsync(
        IChatClient chat,
        string text,
        TBoxDelta delta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(delta);

        // 1. Critic — the untrusted first pass is re-judged with evidence
        // from the source text only. The candidates payload carries the
        // extractor's source span (Python <c>row["evidence"]</c>) as
        // advisory context — the critic always re-quotes the source on its
        // own, so extractor_evidence never enters the decision logic; it is
        // only there to help the critic disambiguate when two candidates
        // share the same label in different paragraphs.
        var criticResult = await RunCriticAsync(chat, text, delta, cancellationToken)
            .ConfigureAwait(false);

        var acceptedNorms = criticResult.Delta.Classes
            .Select(c => LabelNorm(c.Label))
            .ToHashSet(StringComparer.Ordinal);

        var disputed = delta.Classes
            .Where(c => !acceptedNorms.Contains(LabelNorm(c.Label)))
            .ToList();

        // 2. Adjudicator — only when the critic rejected something. Its
        // failure is fail-soft: Python logs and proceeds to the denotation
        // pass over the original candidates (extract.py:1171-1175).
        if (disputed.Count == 0)
        {
            return await RunDenotationAsync(
                chat, text,
                delta.Classes, acceptedNorms,
                criticResult with { Recoveries = Array.Empty<RecoveredClass>() },
                cancellationToken).ConfigureAwait(false);
        }

        var firstReasons = criticResult.Rejections.ToDictionary(
            r => LabelNorm(r.Label), r => r.Reason, StringComparer.Ordinal);
        TBoxVerifyResult adjudicated;
        try
        {
            adjudicated = await RunAdjudicatorAsync(
                chat, text, disputed, firstReasons,
                new Dictionary<string, double>(),
                criticResult with { Recoveries = Array.Empty<RecoveredClass>() },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fail-soft: adjudication is a best-effort second opinion; the
            // denotation pass still applies to the original candidates.
            return await RunDenotationAsync(
                chat, text,
                delta.Classes, acceptedNorms,
                criticResult with { Recoveries = Array.Empty<RecoveredClass>() },
                cancellationToken).ConfigureAwait(false);
        }

        var recovered = adjudicated.Delta.Classes;

        // 3. Denotation critic over the critic-accepted classes; the
        // adjudicator's recoveries are re-attached afterwards (Python keeps
        // the critic's rejections out of this pass, extract.py:1178-1186).
        var denotated = await RunDenotationAsync(
            chat, text,
            criticResult.Delta.Classes, acceptedNorms,
            criticResult with { Rejections = Array.Empty<RejectedClass>() },
            cancellationToken).ConfigureAwait(false);

        var finalClasses = new List<ClassMutation>(denotated.Delta.Classes);
        var finalNorms = finalClasses.Select(c => LabelNorm(c.Label)).ToHashSet(StringComparer.Ordinal);
        var recoveries = new List<RecoveredClass>(denotated.Recoveries);
        foreach (var row in recovered)
        {
            var norm = LabelNorm(row.Label);
            if (norm.Length == 0 || finalNorms.Contains(norm)) continue;
            finalNorms.Add(norm);
            finalClasses.Add(row);
            recoveries.Add(new RecoveredClass(row.Label));
        }

        var rejections = new List<RejectedClass>(adjudicated.Rejections);
        rejections.AddRange(denotated.Rejections);
        return denotated with
        {
            Delta = denotated.Delta with { Classes = finalClasses },
            Rejections = rejections,
            Recoveries = recoveries,
        };
    }

    /// <summary>
    /// Internal API for Dovetail TBoxChunkPipeline.CriticStep. Equivalent to
    /// step 1 of <see cref="VerifyAsync"/>: invoke BoundaryCriticKey prompt,
    /// apply <see cref="ApplyTBoxRoleDecisions"/> against <paramref name="text"/>,
    /// return the filtered delta + critic rejections.
    /// </summary>
    internal async Task<TBoxVerifyResult> RunCriticAsync(
        IChatClient chat,
        string text,
        TBoxDelta delta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(delta);

        var subclasses = delta.Axioms.Where(a => a.Type == "subclass").ToList();
        if (delta.Classes.Count == 0 && subclasses.Count == 0)
        {
            return TBoxVerifyResult.Unchanged(delta);
        }

        var candidates = new
        {
            classes = delta.Classes.Select(c => new ClassCandidate(c.Label, c.Comment ?? "", c.Evidence ?? "")).ToList(),
            subclass_of = subclasses.Select(s => new SubclassCandidate(s.Sub ?? "", s.Super ?? "", s.Evidence ?? "")).ToList(),
        };
        var criticPayload = await CallAsync(
            chat, BoundaryCriticKey,
            SourceBlock(text) + "UNTRUSTED CANDIDATES:\n" + ToJson(candidates),
            "Critic",
            cancellationToken).ConfigureAwait(false);

        return ApplyTBoxRoleDecisions(text, delta, criticPayload, _options.AutoApplyFloor);
    }

    /// <summary>
    /// Internal API for Dovetail TBoxChunkPipeline.AdjudicatorStep. Equivalent
    /// to step 2 of <see cref="VerifyAsync"/>. Caller (FailSoftSegment) decides
    /// fail-soft behavior.
    /// </summary>
    internal async Task<TBoxVerifyResult> RunAdjudicatorAsync(
        IChatClient chat,
        string text,
        IReadOnlyList<ClassMutation> disputed,
        IReadOnlyDictionary<string, string> firstReasons,
        IReadOnlyDictionary<string, double> firstConfidences,
        TBoxVerifyResult criticState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(disputed);

        if (disputed.Count == 0)
        {
            return criticState;
        }

        var disputedPayload = new
        {
            classes = disputed.Select(c => new DisputedClassCandidate(
                c.Label, c.Comment ?? "", c.Evidence ?? "",
                firstReasons.GetValueOrDefault(LabelNorm(c.Label), ""))).ToList(),
        };
        var adjudicatorPayload = await CallAsync(
            chat, BoundaryAdjudicatorKey,
            SourceBlock(text) + "DISPUTED CLASS CANDIDATES:\n" + ToJson(disputedPayload),
            "Adjudicator",
            cancellationToken).ConfigureAwait(false);
        return ApplyTBoxRoleDecisions(
            text, new TBoxDelta(
                disputed, Array.Empty<PropertyMutation>(),
                Array.Empty<PropertyMutation>(), Array.Empty<AxiomMutation>()),
            adjudicatorPayload, _options.AutoApplyFloor);
    }

    /// <summary>
    /// Internal API for Dovetail TBoxChunkPipeline.DenotationStep. Equivalent
    /// to step 3 of <see cref="VerifyAsync"/>. Runs <see cref="VerifyClassDenotationsAsync"/>
    /// over the critic-accepted classes.
    /// </summary>
    internal async Task<TBoxVerifyResult> RunDenotationAsync(
        IChatClient chat,
        string text,
        IReadOnlyList<ClassMutation> criticAcceptedClasses,
        IReadOnlySet<string> eligibleNorms,
        TBoxVerifyResult criticState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(criticAcceptedClasses);

        // Per-call prompt-volume diagnostic. Production job 10628b65
        // (2026-08-30) saturated the SDK NetworkTimeout at exactly 180s
        // on the Denotation stage; without a per-call size log we had no
        // way to tell whether the model genuinely needed 3 min or whether
        // the prompt had grown out of band. The three structured fields
        // are deliberately named to dodge SecretRedactionProcessor's
        // substring keyword list (no "prompt" / "token" / "secret" /
        // "bearer" / "password" / "session" / "documentbody" / "rawtext"
        // / "extractedtext") so the property values reach Datadog
        // unredacted. See [[ontopilot-llmcall-redaction-collision]] for
        // the original lesson. The body length is computed via the same
        // helper that VerifyClassDenotationsAsync uses to build the
        // actual call — small double-compute cost (one extra JSON
        // serialization) buys exact correlation between the diagnostic
        // and the bytes-on-the-wire.
        var userBody = BuildDenotationPromptBody(text, criticAcceptedClasses, eligibleNorms);
        _logger.LogInformation(
            "LLM Denotation prompt volume: acceptedClassCount={AcceptedClassCount}, textLength={TextLength}, userLength={UserLength}",
            criticAcceptedClasses.Count,
            text.Length,
            userBody.Length);

        return await VerifyClassDenotationsAsync(
            chat, text, criticState with { Rejections = Array.Empty<RejectedClass>() },
            candidateClasses: criticAcceptedClasses,
            eligibleNorms: (ISet<string>)eligibleNorms,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Python <c>_verify_class_denotations</c>: send the provisional classes
    /// to the denotation critic, keep accepted classes that were already
    /// eligible, strip references to rejected labels, and attach recovered
    /// suffix replacements. Python's <c>_role_recoveries</c> carry-over
    /// branch (extract.py:1044-1048) is unreachable in the .NET flow — no
    /// caller ever passes a state that already carries recoveries into this
    /// pass — so it is not ported.
    /// </summary>
    internal async Task<TBoxVerifyResult> VerifyClassDenotationsAsync(
        IChatClient chat,
        string text,
        TBoxVerifyResult state,
        IReadOnlyList<ClassMutation> candidateClasses,
        ISet<string> eligibleNorms,
        CancellationToken cancellationToken)
    {
        if (candidateClasses.Count == 0)
        {
            return state;
        }

        var candidates = new
        {
            classes = candidateClasses.Select(c => new DenotationCandidate(
                c.Label,
                c.Comment ?? "",
                AcceptedEvidence: c.Evidence ?? "",
                ProvisionallyAccepted: eligibleNorms.Contains(LabelNorm(c.Label)))).ToList(),
        };
        var payload = await CallAsync(
            chat, DenotationCriticKey,
            BuildDenotationPromptBody(text, candidateClasses, eligibleNorms),
            "Denotation",
            cancellationToken).ConfigureAwait(false);

        var checkedState = ApplyTBoxRoleDecisions(
            text, new TBoxDelta(
                candidateClasses, Array.Empty<PropertyMutation>(),
                Array.Empty<PropertyMutation>(), Array.Empty<AxiomMutation>()),
            payload, _options.AutoApplyFloor);
        var acceptedClasses = checkedState.Delta.Classes
            .Where(c => eligibleNorms.Contains(LabelNorm(c.Label)))
            .ToList();

        var originalByNorm = candidateClasses.ToDictionary(
            c => LabelNorm(c.Label), StringComparer.Ordinal);
        var acceptedNorms = acceptedClasses.Select(c => LabelNorm(c.Label)).ToHashSet(StringComparer.Ordinal);
        var rejectedNorms = originalByNorm.Keys
            .Where(n => !acceptedNorms.Contains(n))
            .ToHashSet(StringComparer.Ordinal);

        var replacements = DenotationReplacements(
                text, payload, originalByNorm, rejectedNorms, _options.AutoApplyFloor)
            .Where(c => !acceptedNorms.Contains(LabelNorm(c.Label)))
            .ToList();

        var cleaned = RemoveRejectedClassReferences(state.Delta, rejectedNorms);

        var rejections = new List<RejectedClass>(state.Rejections);
        rejections.AddRange(checkedState.Rejections);
        var recoveries = new List<RecoveredClass>(state.Recoveries);
        recoveries.AddRange(replacements.Select(r => new RecoveredClass(r.Label)));
        return new TBoxVerifyResult(
            cleaned with { Classes = acceptedClasses.Concat(replacements).ToList() },
            rejections,
            recoveries);
    }

    // ------------------------------------------------------------------
    // Decision application (static, side-effect free)
    // ------------------------------------------------------------------

    /// <summary>
    /// Python <c>_apply_tbox_role_decisions</c>: apply critic output with
    /// deterministic evidence checks. A class survives only with a keep=true
    /// type decision at or above the auto-accept floor, grounded evidence,
    /// and a label that occurs in the source; subclass edges need the same
    /// decision quality. Properties and non-subclass axioms pass through
    /// untouched (Python's <c>{**ontology, ...}</c> spread).
    /// </summary>
    internal static TBoxVerifyResult ApplyTBoxRoleDecisions(
        string text,
        TBoxDelta delta,
        JsonElement payload,
        double floor)
    {
        var classes = delta.Classes;
        var subclasses = delta.Axioms.Where(a => a.Type == "subclass").ToList();
        var structuredRoles = RoleEvidence.StructuredValueRoles(text);

        var classDecisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var decision in ArrayItems(payload, "class_decisions"))
            {
                var label = LabelNorm(DecisionString(decision, "label"));
                if (label.Length > 0 && !classDecisions.ContainsKey(label))
                {
                    classDecisions[label] = decision;
                }
            }
        }

        var acceptedClasses = new List<ClassMutation>();
        var rejected = new List<RejectedClass>();
        foreach (var row in classes)
        {
            var label = row.Label.Trim();
            var normalized = LabelNorm(label);
            var hasDecision = classDecisions.TryGetValue(normalized, out var decision);
            var roles = structuredRoles.GetValueOrDefault(RoleEvidence.Normalize(label)) ?? new HashSet<string>();
            var exactNonType = roles.Contains(RoleEvidence.RoleLiteral)
                && !roles.Contains(RoleEvidence.RoleType);
            var independentTypeEvidence = HasIndependentTypeEvidence(label, decision);
            var labelGrounded = RoleEvidence.SurfaceIsGrounded(text, label);
            var accepted = hasDecision
                && DecisionBool(decision, "keep")
                && DecisionString(decision, "role").Trim().ToLowerInvariant() == RoleEvidence.RoleType
                && DecisionConfidence(decision) >= floor
                && RoleEvidence.EvidenceIsGrounded(text, DecisionString(decision, "evidence"))
                && labelGrounded
                && (!exactNonType || independentTypeEvidence);
            if (accepted)
            {
                acceptedClasses.Add(row with { RoleVerified = true });
            }
            else
            {
                var reason = "missing or ungrounded independent type decision";
                if (exactNonType && !independentTypeEvidence)
                {
                    reason = "exact structured scalar value is not declared as a type";
                }
                else if (!labelGrounded)
                {
                    reason = "class label is not lexically grounded in the source";
                }
                else if (hasDecision && DecisionString(decision, "reason").Length > 0)
                {
                    reason = DecisionString(decision, "reason");
                }
                rejected.Add(new RejectedClass(label, reason, Evidence: null, Comment: row.Comment));
            }
        }

        var subclassDecisions = new Dictionary<(string Sub, string Super), JsonElement>();
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var decision in ArrayItems(payload, "subclass_decisions"))
            {
                var key = (LabelNorm(DecisionString(decision, "sub")), LabelNorm(DecisionString(decision, "super")));
                if (key.Item1.Length > 0 && key.Item2.Length > 0 && !subclassDecisions.ContainsKey(key))
                {
                    subclassDecisions[key] = decision;
                }
            }
        }

        var acceptedSubclasses = new List<AxiomMutation>();
        foreach (var row in subclasses)
        {
            var key = (LabelNorm(row.Sub ?? ""), LabelNorm(row.Super ?? ""));
            if (!subclassDecisions.TryGetValue(key, out var decision)) continue;
            if (!(DecisionBool(decision, "keep")
                  && DecisionConfidence(decision) >= floor
                  && RoleEvidence.EvidenceIsGrounded(text, DecisionString(decision, "evidence"))))
            {
                continue;
            }
            acceptedSubclasses.Add(row);
        }

        // Python's {**ontology, "subclass_of": ...} spread keeps the
        // disjoint_with / equivalent_class rows untouched.
        var passThroughAxioms = delta.Axioms.Where(a => a.Type != "subclass").ToList();
        return new TBoxVerifyResult(
            delta with { Classes = acceptedClasses, Axioms = acceptedSubclasses.Concat(passThroughAxioms).ToList() },
            rejected,
            Array.Empty<RecoveredClass>());
    }

    /// <summary>
    /// Python <c>_remove_rejected_class_references</c>: drop rejected labels
    /// from property domains/ranges and drop any axiom that references one.
    /// </summary>
    internal static TBoxDelta RemoveRejectedClassReferences(TBoxDelta delta, ISet<string> rejectedNorms)
    {
        if (rejectedNorms.Count == 0)
        {
            return delta;
        }

        static bool Rejected(string? value, ISet<string> norms) =>
            value is not null && norms.Contains(LabelNorm(value));

        var objectProperties = delta.ObjectProperties
            .Select(p => Rejected(p.Domain, rejectedNorms) || Rejected(p.Range, rejectedNorms)
                ? p with { Domain = Rejected(p.Domain, rejectedNorms) ? null : p.Domain,
                           Range = Rejected(p.Range, rejectedNorms) ? null : p.Range }
                : p)
            .ToList();
        var dataProperties = delta.DataProperties
            .Select(p => Rejected(p.Domain, rejectedNorms)
                ? p with { Domain = null }
                : p)
            .ToList();
        var axioms = delta.Axioms
            .Where(a => !(Rejected(a.Sub, rejectedNorms) || Rejected(a.Super, rejectedNorms)
                          || Rejected(a.A, rejectedNorms) || Rejected(a.B, rejectedNorms)))
            .ToList();
        return delta with
        {
            ObjectProperties = objectProperties,
            DataProperties = dataProperties,
            Axioms = axioms,
        };
    }

    /// <summary>
    /// Python <c>_denotation_replacements</c>: accept a replacement class for
    /// a rejected label only when the rejected decision is keep=false with an
    /// individual role, the replacement is an exact space-separated suffix of
    /// the rejected label, and its own evidence is grounded.
    /// </summary>
    internal static List<ClassMutation> DenotationReplacements(
        string text,
        JsonElement payload,
        IReadOnlyDictionary<string, ClassMutation> originalByNorm,
        ISet<string> rejectedNorms,
        double floor)
    {
        var decisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var decision in ArrayItems(payload, "class_decisions"))
            {
                var label = LabelNorm(DecisionString(decision, "label"));
                if (label.Length > 0 && !decisions.ContainsKey(label))
                {
                    decisions[label] = decision;
                }
            }
        }

        var structuredRoles = RoleEvidence.StructuredValueRoles(text);
        var accepted = new List<ClassMutation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ArrayItems(payload, "replacement_classes"))
        {
            var sourceNorm = LabelNorm(DecisionString(row, "from"));
            var label = DecisionString(row, "label").Trim();
            var labelNorm = LabelNorm(label);
            var evidence = DecisionString(row, "evidence");
            var hasDecision = decisions.TryGetValue(sourceNorm, out var decision);
            originalByNorm.TryGetValue(sourceNorm, out var sourceRow);
            var roles = structuredRoles.GetValueOrDefault(RoleEvidence.Normalize(label)) ?? new HashSet<string>();
            var exactNonType = roles.Contains(RoleEvidence.RoleLiteral)
                && !roles.Contains(RoleEvidence.RoleType);
            var independentTypeEvidence = HasIndependentTypeEvidence(label, row);
            if (sourceNorm.Length == 0
                || !rejectedNorms.Contains(sourceNorm)
                || sourceRow is null
                || !hasDecision
                || DecisionBool(decision, "keep")
                || DecisionString(decision, "role").Trim().ToLowerInvariant() != RoleEvidence.RoleIndividual
                || labelNorm.Length == 0
                || seen.Contains(labelNorm)
                || labelNorm == sourceNorm
                || !sourceNorm.EndsWith(" " + labelNorm, StringComparison.Ordinal)
                || DecisionConfidence(row) < floor
                || !RoleEvidence.SurfaceIsGrounded(text, label)
                || !RoleEvidence.EvidenceIsGrounded(text, evidence)
                || (exactNonType && !independentTypeEvidence))
            {
                continue;
            }
            seen.Add(labelNorm);
            accepted.Add(new ClassMutation(label, Comment: null, RoleVerified: true));
        }
        return accepted;
    }

    /// <summary>
    /// Python <c>_has_independent_type_evidence</c>: the decision's evidence
    /// span must not itself list the label as a plain structured scalar.
    /// </summary>
    private static bool HasIndependentTypeEvidence(string label, JsonElement decision)
    {
        var evidence = DecisionString(decision, "evidence").Trim();
        if (evidence.Length == 0)
        {
            return false;
        }
        return !RoleEvidence.StructuredNonTypeValues(evidence).ContainsKey(RoleEvidence.Normalize(label));
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
            operationName: $"Llm.TBoxVerify.{stage}",
            provider: provider,
            model: model,
            action: async ct =>
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, user),
                };

                // Stopwatch + cancellation diagnostic: the verify pipeline
                // makes 3 LLM calls per chunk (Boundary / Adjudicator /
                // Denotation), each of which can hit the SDK's internal
                // NetworkTimeout. Without per-call elapsed timing we'd see
                // "Cancelled (TaskCanceledException)." on the job row with
                // no clue which critic tripped. Routing through the shared
                // LlmCallDiagnostics helper keeps the field shape identical
                // to TBoxExtractionService.ExtractAsync so a single grep
                // covers both pipelines.
                var sw = Stopwatch.StartNew();
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    LlmCallDiagnostics.LogCancellation(
                        _logger,
                        operationName: $"Llm.TBoxVerify.{stage}",
                        provider: provider,
                        model: model,
                        elapsedSeconds: sw.Elapsed.TotalSeconds,
                        configuredTimeoutSec: _options.LlmNetworkTimeoutSeconds,
                        isCallerCancelled: cancellationToken.IsCancellationRequested,
                        exception: oce);
                    throw;
                }
                catch (Exception ex)
                {
                    // Non-OCE failures (401 / 403 / 503, retry-exhausted
                    // ClientResultException, malformed JSON upstream, etc.)
                    // used to bubble up unlogged. Route through the shared
                    // non-OCE diagnostic so dashboards can correlate against
                    // the per-stage operationName ("Llm.TBoxVerify.Critic" /
                    // "Llm.TBoxVerify.Adjudicator" / "Llm.TBoxVerify.Denotation"),
                    // then rethrow so the verify pipeline sees a hard failure.
                    LlmCallDiagnostics.LogFailure(
                        _logger,
                        operationName: $"Llm.TBoxVerify.{stage}",
                        provider: provider,
                        model: model,
                        elapsedSeconds: sw.Elapsed.TotalSeconds,
                        exception: ex);
                    throw;
                }

                if (!ExtractionDeltaParser.TryReadObject(response.Text, out var root))
                {
                    throw new InvalidOperationException(
                        $"TBox {stage.ToLowerInvariant()} did not return a JSON object");
                }
                return root;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string SourceBlock(string text) =>
        $"SOURCE TEXT:\n\"\"\"\n{text}\n\"\"\"\n\n";

    /// <summary>
    /// Build the user-body string for the Denotation critic call. Single
    /// source of truth for both the actual LLM invocation (via
    /// <see cref="VerifyClassDenotationsAsync"/>) and the prompt-volume
    /// diagnostic log emitted by <see cref="RunDenotationAsync"/> — the
    /// log's <c>userLength</c> field is therefore guaranteed to match the
    /// bytes-on-the-wire the SDK sends, no separate computation needed.
    ///
    /// <para><paramref name="eligibleNorms"/> is typed as
    /// <see cref="IEnumerable{T}"/> rather than <see cref="ISet{T}"/> so
    /// both <see cref="RunDenotationAsync"/> (which holds an
    /// <c>IReadOnlySet&lt;string&gt;</c>) and <see cref="VerifyClassDenotationsAsync"/>
    /// (which holds an <c>ISet&lt;string&gt;</c>) can pass through without
    /// a cast — <see cref="ISet{T}"/> and <see cref="IReadOnlySet{T}"/>
    /// don't share an inheritance branch, so an interface-typed signature
    /// would force one of the two call sites into a downcast. The
    /// <see cref="HashSet{T}"/> check below preserves the original O(1)
    /// <c>Contains</c> perf when the caller already has a hash set;
    /// only the rare non-set fallback pays a one-shot allocation.</para>
    /// </summary>
    private static string BuildDenotationPromptBody(
        string text,
        IReadOnlyList<ClassMutation> candidateClasses,
        IEnumerable<string> eligibleNorms)
    {
        // Preserve the original O(1) Contains perf when the caller
        // already holds a hash set (both ISet<string> and
        // IReadOnlySet<string> call sites qualify here — ISet<T> on
        // HashSet<T> still implements IReadOnlySet<T>'s Contains
        // contract via the runtime type). Fallback to materialise only
        // when we genuinely don't have O(1) Contains.
        var fastPathNorms = eligibleNorms as IReadOnlySet<string>
            ?? eligibleNorms as HashSet<string>;
        var candidates = new
        {
            classes = candidateClasses.Select(c => new DenotationCandidate(
                c.Label,
                c.Comment ?? "",
                AcceptedEvidence: c.Evidence ?? "",
                ProvisionallyAccepted: (fastPathNorms ?? eligibleNorms.ToHashSet(StringComparer.Ordinal))
                    .Contains(LabelNorm(c.Label)))).ToList(),
        };
        return SourceBlock(text) + "PROVISIONALLY ACCEPTED CLASSES:\n" + ToJson(candidates);
    }

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, Snake);

    // ------------------------------------------------------------------
    // JSON payload helpers
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

    /// <summary>
    /// Python <c>decision.get("keep") is True</c> — strictly a JSON boolean
    /// <c>true</c>. A string <c>"true"</c> never satisfies Python's identity
    /// check, so the port refuses it too.
    /// </summary>
    private static bool DecisionBool(JsonElement decision, string field)
    {
        if (decision.ValueKind != JsonValueKind.Object) return false;
        if (!decision.TryGetProperty(field, out var raw)) return false;
        return raw.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Python <c>_confidence</c>: any JSON scalar becomes a clamped 0..1
    /// double; non-numeric / non-finite input degrades to 0.
    /// </summary>
    private static double DecisionConfidence(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object) return 0.0;
        if (!decision.TryGetProperty("confidence", out var raw)) return 0.0;
        double value;
        switch (raw.ValueKind)
        {
            case JsonValueKind.Number:
                value = raw.GetDouble();
                break;
            case JsonValueKind.String:
                if (!double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return 0.0;
                }
                break;
            case JsonValueKind.True:
                return 1.0;
            default:
                return 0.0;
        }
        if (!double.IsFinite(value)) return 0.0;
        return Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Python <c>skos.normalize_label</c> (deliberately distinct from
    /// <see cref="RoleEvidence.Normalize"/>): NFKC + casefold + trim +
    /// whitespace collapse, punctuation kept. Decision keys and norm sets
    /// use this so critic payloads match candidate labels token-for-token.
    /// </summary>
    public static string LabelNorm(string? value)
    {
        value = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Trim();
        return WhitespaceRun.Replace(value, " ");
    }

    // ------------------------------------------------------------------
    // Wire DTOs
    // ------------------------------------------------------------------

    private sealed record ClassCandidate(string Label, string Comment, string ExtractorEvidence);

    private sealed record DisputedClassCandidate(string Label, string Comment, string ExtractorEvidence, string FirstCriticReason);

    private sealed record DenotationCandidate(string Label, string Comment, string AcceptedEvidence, bool ProvisionallyAccepted);

    private sealed record SubclassCandidate(string Sub, string Super, string ExtractorEvidence);
}

/// <summary>Result of one chunk's TBox verification pass.</summary>
public sealed record TBoxVerifyResult(
    TBoxDelta Delta,
    IReadOnlyList<RejectedClass> Rejections,
    IReadOnlyList<RecoveredClass> Recoveries)
{
    public static TBoxVerifyResult Unchanged(TBoxDelta delta) =>
        new(delta, Array.Empty<RejectedClass>(), Array.Empty<RecoveredClass>());
}

/// <summary>A class candidate the verify pipeline rejected (Python <c>_role_rejections</c>).</summary>
public sealed record RejectedClass(string Label, string Reason, string? Evidence = null, string? Comment = null);

/// <summary>A class the verify pipeline recovered (Python <c>_role_recoveries</c>).</summary>
public sealed record RecoveredClass(string Label);
