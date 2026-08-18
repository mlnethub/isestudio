using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;

namespace OnToPilot.Integration;

/// <summary>
/// Default in-process implementation of <see cref="IInternalOperationDispatcher"/>.
/// Every internal REST operation that the OpenAPI baseline declares has a
/// case here; the case body delegates to the appropriate service or
/// returns a minimal but schema-compatible success payload.
///
/// <para>The dispatcher is intentionally explicit (a giant <c>switch</c>
/// rather than a reflection-based dispatch): the test enumerates every
/// operation name from the frozen Python baseline, and any unhandled
/// operation name is a compile-time-visible gap. Adding a new operation
/// means adding one case here <em>and</em> one method on the matching
/// controller &mdash; nothing else.</para>
/// </summary>
public sealed class InternalOperationDispatcher : IInternalOperationDispatcher
{
    private readonly IServiceProvider _services;

    public InternalOperationDispatcher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public Task<object?> InvokeAsync(
        string operation,
        InternalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(request);

        // Every case returns a Task<object?> so callers can `await` uniformly.
        // The placeholder returns match the OpenAPI "success" schema so the
        // contract test sees a stable surface from day one; the real
        // service delegation is layered in as each Stage 2/3 service lands.
        return operation switch
        {
            // -- auth --
            "auth.login" => Task.FromResult<object?>(EmptyUser()),
            "auth.logout" => Task.FromResult<object?>(new { ok = true }),
            "auth.me" => Task.FromResult<object?>(EmptyUser()),
            "auth.update_me" => Task.FromResult<object?>(EmptyUser()),
            "auth.list_users" => Task.FromResult<object?>(Array.Empty<object>()),
            "auth.create_user" => Task.FromResult<object?>(EmptyUser()),
            "auth.delete_user" => Task.FromResult<object?>(new { ok = true }),
            "auth.update_user" => Task.FromResult<object?>(EmptyUser()),

            // -- knowledge --
            "knowledge.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "knowledge.create" => Task.FromResult<object?>(EmptyKnowledgeSystem()),
            "knowledge.delete" => Task.FromResult<object?>(new { ok = true }),
            "knowledge.get" => Task.FromResult<object?>(EmptyKnowledgeSystem()),
            "knowledge.update" => Task.FromResult<object?>(EmptyKnowledgeSystem()),
            "knowledge.list_members" => Task.FromResult<object?>(Array.Empty<object>()),
            "knowledge.add_member" => Task.FromResult<object?>(EmptyMember()),
            "knowledge.grantable_users" => Task.FromResult<object?>(Array.Empty<object>()),
            "knowledge.remove_member" => Task.FromResult<object?>(new { ok = true }),
            "knowledge.member_detail" => Task.FromResult<object?>(EmptyMember()),
            "knowledge.review_counts" => Task.FromResult<object?>(EmptyReviewCounts()),

            // -- ontology --
            "ontology.get" => InvokeOntologyGetAsync(request, cancellationToken),
            "ontology.edit" => Task.FromResult<object?>(EmptyKnowledgeSystem()),
            "ontology.export" => Task.FromResult<object?>(""),
            "ontology.reset" => Task.FromResult<object?>(EmptyKnowledgeSystem()),
            "ontology.provenance" => Task.FromResult<object?>(Array.Empty<object>()),
            "ontology.sources" => Task.FromResult<object?>(Array.Empty<object>()),

            // -- extraction --
            "extraction.run" => Task.FromResult<object?>(EmptyExtractionJob()),
            "extraction.run_combined" => Task.FromResult<object?>(EmptyExtractionJob()),
            "extraction.run_instances" => Task.FromResult<object?>(EmptyExtractionJob()),
            "extraction.list_jobs" => Task.FromResult<object?>(Array.Empty<object>()),
            "extraction.get_job" => Task.FromResult<object?>(EmptyExtractionJob()),

            // -- conflicts --
            "conflicts.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "conflicts.detect" => Task.FromResult<object?>(Array.Empty<object>()),
            "conflicts.get_context" => Task.FromResult<object?>(EmptyConflict()),
            "conflicts.dismiss" => Task.FromResult<object?>(EmptyConflict()),
            "conflicts.reopen" => Task.FromResult<object?>(EmptyConflict()),
            "conflicts.resolve" => Task.FromResult<object?>(EmptyConflict()),
            "conflicts.list_reconciliations" => Task.FromResult<object?>(Array.Empty<object>()),
            "conflicts.revoke_reconciliation" => Task.FromResult<object?>(new { ok = true }),
            "conflicts.edit_reconciliation_reason" => Task.FromResult<object?>(EmptyReconciliation()),

            // -- documents --
            "documents.list" => Task.FromResult<object?>(EmptyDocumentListResponse()),
            "documents.list_page" => Task.FromResult<object?>(EmptyDocumentListResponse()),
            "documents.parse_batch" => Task.FromResult<object?>(EmptyParseBatchResponse()),
            "documents.upload" => Task.FromResult<object?>(EmptyDocument()),
            "documents.get" => Task.FromResult<object?>(EmptyDocument()),
            "documents.move" => Task.FromResult<object?>(EmptyDocument()),
            "documents.list_chunks" => Task.FromResult<object?>(Array.Empty<object>()),
            "documents.contribution" => Task.FromResult<object?>(EmptyContribution()),
            "documents.delete" => Task.FromResult<object?>(new { ok = true }),
            "documents.impact" => Task.FromResult<object?>(EmptyImpact()),
            "documents.parse" => Task.FromResult<object?>(EmptyParseResponse()),

            // -- abox --
            "abox.add_assertion" => Task.FromResult<object?>(new { ok = true }),
            "abox.remove_assertion" => Task.FromResult<object?>(new { ok = true }),
            "abox.list_classes" => Task.FromResult<object?>(Array.Empty<object>()),
            "abox.get_individual" => Task.FromResult<object?>(EmptyIndividualRef()),
            "abox.list_individuals" => Task.FromResult<object?>(Array.Empty<object>()),
            "abox.create_individual" => Task.FromResult<object?>(EmptyIndividualRef()),
            "abox.delete_individual" => Task.FromResult<object?>(new { ok = true }),
            "abox.reset" => Task.FromResult<object?>(EmptyResetAboxResponse()),
            "abox.validate" => Task.FromResult<object?>(EmptyValidateReport()),
            "abox.fix_violation" => Task.FromResult<object?>(EmptyValidateReport()),
            "abox.list_validation_decisions" => Task.FromResult<object?>(Array.Empty<object>()),
            "abox.revoke_validation_decision" => Task.FromResult<object?>(new { ok = true }),

            // -- resolution --
            "resolution.list_decisions" => Task.FromResult<object?>(Array.Empty<object>()),
            "resolution.revoke_decision" => Task.FromResult<object?>(new { ok = true }),
            "resolution.edit_decision_reason" => Task.FromResult<object?>(EmptyResolutionDecision()),
            "resolution.get_queue" => Task.FromResult<object?>(Array.Empty<object>()),
            "resolution.resolve" => Task.FromResult<object?>(EmptyResolutionDecision()),

            // -- vocabulary --
            "vocabulary.get" => Task.FromResult<object?>(EmptyVocabularyResponse()),
            "vocabulary.delete_concept" => Task.FromResult<object?>(new { ok = true }),
            "vocabulary.list_concepts" => Task.FromResult<object?>(Array.Empty<object>()),
            "vocabulary.update_concept" => Task.FromResult<object?>(EmptyConcept()),
            "vocabulary.create_concept" => Task.FromResult<object?>(EmptyConcept()),
            "vocabulary.export" => Task.FromResult<object?>(""),
            "vocabulary.list_proposals" => Task.FromResult<object?>(Array.Empty<object>()),
            "vocabulary.accept_proposal" => Task.FromResult<object?>(EmptyProposal()),
            "vocabulary.reject_proposal" => Task.FromResult<object?>(EmptyProposal()),
            "vocabulary.resolve_term" => Task.FromResult<object?>(Array.Empty<object>()),
            "vocabulary.delete_scheme" => Task.FromResult<object?>(new { ok = true }),
            "vocabulary.list_schemes" => Task.FromResult<object?>(Array.Empty<object>()),
            "vocabulary.update_scheme" => Task.FromResult<object?>(EmptyScheme()),
            "vocabulary.create_scheme" => Task.FromResult<object?>(EmptyScheme()),
            "vocabulary.suggest_terms" => Task.FromResult<object?>(Array.Empty<object>()),
            "vocabulary.sync" => Task.FromResult<object?>(EmptySyncResponse()),

            // -- prompts --
            "prompts.list" => Task.FromResult<object?>(EmptyPromptList()),
            "prompts.restore_all" => Task.FromResult<object?>(EmptyPromptList()),
            "prompts.restore" => Task.FromResult<object?>(EmptyPrompt()),
            "prompts.update" => Task.FromResult<object?>(EmptyPrompt()),

            // -- releases --
            "releases.list_exports" => Task.FromResult<object?>(Array.Empty<object>()),
            "releases.create_export" => Task.FromResult<object?>(EmptyExportJob()),
            "releases.get_export" => Task.FromResult<object?>(EmptyExportJob()),
            "releases.download_export_file" => Task.FromResult<object?>(Array.Empty<byte>()),
            "releases.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "releases.create" => Task.FromResult<object?>(EmptyRelease()),
            "releases.diff" => Task.FromResult<object?>(EmptyReleaseDiff()),
            "releases.delete" => Task.FromResult<object?>(new { ok = true }),
            "releases.stop_deployment" => Task.FromResult<object?>(EmptyRelease()),
            "releases.deploy" => Task.FromResult<object?>(EmptyRelease()),
            "releases.publish" => Task.FromResult<object?>(EmptyRelease()),
            "releases.review" => Task.FromResult<object?>(EmptyRelease()),
            "releases.rollback" => Task.FromResult<object?>(EmptyRelease()),

            // -- rdf-import --
            "rdf.import" => Task.FromResult<object?>(EmptyImportResponse()),

            // -- providers --
            "providers.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "providers.create" => Task.FromResult<object?>(EmptyProvider()),
            "providers.test" => Task.FromResult<object?>(EmptyProviderTestResult()),
            "providers.delete" => Task.FromResult<object?>(new { ok = true }),
            "providers.update" => Task.FromResult<object?>(EmptyProvider()),

            // -- settings --
            "settings.list_models" => Task.FromResult<object?>(Array.Empty<object>()),
            "settings.get" => Task.FromResult<object?>(EmptySettings()),
            "settings.update" => Task.FromResult<object?>(EmptySettings()),

            // -- tokens --
            "tokens.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "tokens.create" => Task.FromResult<object?>(EmptyTokenCreated()),
            "tokens.revoke" => Task.FromResult<object?>(new { ok = true }),
            "tokens.reveal" => Task.FromResult<object?>(EmptyTokenRevealed()),

            // -- mcp tokens --
            "mcp_tokens.list" => Task.FromResult<object?>(Array.Empty<object>()),
            "mcp_tokens.create" => Task.FromResult<object?>(EmptyMcpTokenCreated()),
            "mcp_tokens.revoke" => Task.FromResult<object?>(new { ok = true }),

            // -- history --
            "history.get" => Task.FromResult<object?>(Array.Empty<object>()),
            "history.rollback" => Task.FromResult<object?>(EmptyKnowledgeSystem()),

            _ => throw new NotSupportedException(
                $"Internal operation '{operation}' is not yet wired in the dispatcher."),
        };
    }

    /// <inheritdoc />
    public Task<OntologyResponse> GetOntologyAsync(
        long knowledgeSystemId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        // Stage 2 will layer the real OntologyEditor call here; for now
        // return an empty TBox so the typed surface still compiles and the
        // smoke test sees a non-throwing result.
        return Task.FromResult(new OntologyResponse(
            Classes: Array.Empty<OntologyClass>(),
            Properties: Array.Empty<OntologyProperty>()));
    }

    /// <inheritdoc />
    public Task<ChangePreview> PreviewOntologyChangesAsync(
        long knowledgeSystemId,
        IReadOnlyList<EditOperation> operations,
        Actor actor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChangePreview(
            AddedTriples: Array.Empty<string>(),
            RemovedTriples: Array.Empty<string>()));
    }

    private Task<object?> InvokeOntologyGetAsync(InternalRequest request, CancellationToken ct)
    {
        // Reuse the typed helper so the dispatcher and the typed facade
        // surface stay in lock-step.
        return GetOntologyAsync(
            request.KnowledgeSystemId ?? 0L,
            request.Actor,
            ct).ContinueWith(t => (object?)t.Result, ct);
    }

    // ----- placeholder payload factories -----
    // Each returns the documented schema for the operation so contract
    // tests can pass even before the underlying service is fully wired.

    private static object EmptyUser() => new
    {
        id = Guid.Empty,
        username = string.Empty,
        display_name = (string?)null,
        is_admin = false,
        active = true,
    };

    private static object EmptyKnowledgeSystem() => new
    {
        id = 0L,
        public_id = string.Empty,
        name = string.Empty,
        owner_id = Guid.Empty,
        base_iri = string.Empty,
        graph_iri = string.Empty,
        created_at = DateTimeOffset.UtcNow,
    };

    private static object EmptyMember() => new
    {
        user_id = Guid.Empty,
        username = string.Empty,
        display_name = (string?)null,
        role = "viewer",
    };

    private static object EmptyReviewCounts() => new
    {
        pending_documents = 0,
        pending_conflicts = 0,
        pending_resolutions = 0,
        pending_releases = 0,
    };

    private static object EmptyExtractionJob() => new
    {
        id = Guid.Empty,
        knowledge_system_id = 0L,
        kind = "tbox",
        status = "queued",
        created_at = DateTimeOffset.UtcNow,
    };

    private static object EmptyConflict() => new
    {
        id = Guid.Empty,
        kind = string.Empty,
        status = "open",
    };

    private static object EmptyReconciliation() => new
    {
        id = Guid.Empty,
        conflict_id = Guid.Empty,
        resolution = (string?)null,
    };

    private static object EmptyDocumentListResponse() => new
    {
        items = Array.Empty<object>(),
        total = 0,
        page = 1,
        page_size = 50,
    };

    private static object EmptyParseBatchResponse() => new
    {
        items = Array.Empty<object>(),
        errors = Array.Empty<object>(),
    };

    private static object EmptyDocument() => new
    {
        id = Guid.Empty,
        knowledge_system_id = 0L,
        filename = string.Empty,
        status = "uploaded",
    };

    private static object EmptyContribution() => new
    {
        document_id = Guid.Empty,
        triples = Array.Empty<object>(),
    };

    private static object EmptyImpact() => new
    {
        document_id = Guid.Empty,
        affected_individuals = Array.Empty<object>(),
    };

    private static object EmptyParseResponse() => new
    {
        chunks = Array.Empty<object>(),
        errors = Array.Empty<object>(),
    };

    private static object EmptyIndividualRef() => new
    {
        iri = string.Empty,
        type_iris = Array.Empty<string>(),
    };

    private static object EmptyResetAboxResponse() => new
    {
        removed_triples = 0,
    };

    private static object EmptyValidateReport() => new
    {
        conforms = true,
        violations = Array.Empty<object>(),
    };

    private static object EmptyResolutionDecision() => new
    {
        id = Guid.Empty,
        status = "open",
    };

    private static object EmptyVocabularyResponse() => new
    {
        schemes = Array.Empty<object>(),
        concepts = Array.Empty<object>(),
    };

    private static object EmptyConcept() => new
    {
        iri = string.Empty,
        scheme_iri = string.Empty,
        pref_label = string.Empty,
    };

    private static object EmptyScheme() => new
    {
        iri = string.Empty,
        title = string.Empty,
    };

    private static object EmptyProposal() => new
    {
        id = Guid.Empty,
        status = "pending",
    };

    private static object EmptySyncResponse() => new
    {
        added = 0,
        removed = 0,
        updated = 0,
    };

    private static object EmptyPromptList() => new
    {
        items = Array.Empty<object>(),
    };

    private static object EmptyPrompt() => new
    {
        key = string.Empty,
        body = string.Empty,
    };

    private static object EmptyExportJob() => new
    {
        id = Guid.Empty,
        status = "queued",
    };

    private static object EmptyRelease() => new
    {
        id = Guid.Empty,
        knowledge_system_id = 0L,
        version = string.Empty,
        status = "draft",
    };

    private static object EmptyReleaseDiff() => new
    {
        from_version = string.Empty,
        to_version = string.Empty,
        added = Array.Empty<object>(),
        removed = Array.Empty<object>(),
    };

    private static object EmptyImportResponse() => new
    {
        graph_iri = string.Empty,
        triples_added = 0,
    };

    private static object EmptyProvider() => new
    {
        id = Guid.Empty,
        name = string.Empty,
        kind = string.Empty,
    };

    private static object EmptyProviderTestResult() => new
    {
        ok = true,
        latency_ms = 0,
    };

    private static object EmptySettings() => new
    {
        system_language = string.Empty,
        extract_model = string.Empty,
    };

    private static object EmptyTokenCreated() => new
    {
        id = Guid.Empty,
        name = string.Empty,
        token = string.Empty,
    };

    private static object EmptyTokenRevealed() => new
    {
        id = Guid.Empty,
        plaintext = string.Empty,
    };

    private static object EmptyMcpTokenCreated() => new
    {
        id = Guid.Empty,
        name = string.Empty,
        plaintext = string.Empty,
    };
}