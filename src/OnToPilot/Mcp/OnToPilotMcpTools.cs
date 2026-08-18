using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OnToPilot.Application.Foundation;
using OnToPilot.Application.Integration;
using OnToPilot.Authentication;
using OnToPilot.Authorization;

namespace OnToPilot.Mcp;

/// <summary>
/// One MCP <c>tools/list</c> entry exposed by the .NET server. The shape
/// mirrors the frozen Python baseline: a stable <c>Name</c>, a short
/// <c>Description</c>, and the list of <c>RequiredScopes</c> the
/// transport enforces on every call. Equality is structural (record) so
/// the parity gate can use the default <see cref="IEquatable{T}"/>
/// implementation when diffing the two inventories.
/// </summary>
/// <param name="Name">Tool name as exposed to MCP clients (snake_case).</param>
/// <param name="Description">Tool description surfaced to LLM clients.</param>
/// <param name="RequiredScopes">
/// Scopes the middleware / accessor verifies before the tool body runs.
/// Empty for read-only tools that the <c>mcp:read</c> scope already
/// covers.
/// </param>
public sealed record McpToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string> RequiredScopes);

/// <summary>
/// Tool surface exposed by the OnToPilot MCP transport. Every tool
/// method is decorated with <see cref="McpServerToolAttribute"/> so the
/// SDK's <c>WithTools&lt;OnToPilotMcpTools&gt;()</c> registration picks
/// it up via reflection; the <see cref="Inventory"/> static method
/// returns the canonical list the parity test diffs against the frozen
/// Python baseline.
///
/// <para>The tool bodies route through
/// <see cref="McpPrincipalAccessor"/> on every call: the bearer token
/// is re-verified, the user's active flag is re-read, and the effective
/// KS role is re-resolved (no caching). This is what lets the
/// <c>Existing_token_loses_write_access_after_membership_downgrade</c>
/// test prove a role downgrade takes effect on the next tool call
/// without invalidating the bearer.</para>
/// </summary>
[McpServerToolType]
public sealed class OnToPilotMcpTools
{
    private readonly IIntegrationApiFacade _facade;
    private readonly McpPrincipalAccessor _accessor;
    private readonly ILogger<OnToPilotMcpTools> _logger;

    /// <summary>
    /// Maximum number of ontology edits a single destructive tool call
    /// may commit. Matches the Python backend's brief-mandated cap; the
    /// SDK surfaces an error when this is exceeded so the LLM can ask
    /// the operator to chunk the change set.
    /// </summary>
    public const int MaxEditsPerDestructiveCall = 50;

    /// <summary>
    /// Maximum total N-Quads size (in bytes) a single destructive tool
    /// call may commit. Matches the Python backend's 200 KiB cap.
    /// </summary>
    public const int MaxDestructivePayloadBytes = 200 * 1024;

    /// <summary>DI constructor.</summary>
    public OnToPilotMcpTools(
        IIntegrationApiFacade facade,
        McpPrincipalAccessor accessor,
        ILogger<OnToPilotMcpTools> logger)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);
        _facade = facade;
        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// Canonical inventory of the 20 baseline tools the .NET MCP
    /// transport advertises on <c>tools/list</c>. The list is sorted by
    /// name (matching <see cref="OnToPilot.ApiContract.Tests.Baseline.BaselineLoader.McpTools"/>)
    /// so the parity test's <c>expected.Except(actual)</c> diff lands in
    /// alphabetical order.
    /// </summary>
    public static IReadOnlyList<McpToolDescriptor> Inventory()
    {
        var tools = new List<McpToolDescriptor>
        {
            Read("apply_instance_change",
                "Create/delete an individual or add/remove an ABox assertion in the mutable workspace.",
                McpTokenScopes.McpWrite),
            Destructive("apply_ontology_changes",
                "Atomically apply a previewed TBox change set to the mutable workspace and audit it.",
                McpTokenScopes.McpWrite),
            Destructive("apply_vocabulary_change",
                "Create, update, delete, or synchronize SKOS schemes and concepts.",
                McpTokenScopes.McpWrite),
            Destructive("decide_review_item",
                "Resolve or dismiss a governance queue item using the same audited application services.",
                McpTokenScopes.McpWrite),
            Read("get_history",
                "Read the audited change history for the bound knowledge system."),
            Read("get_individual",
                "Read one individual with types, assertions, and source evidence."),
            Read("get_ontology",
                "Read the current mutable TBox as structured classes, properties, axioms, and labels."),
            Read("get_workspace_context",
                "Get the bound workspace, current user role, graph statistics, and governance blockers."),
            Read("list_documents",
                "List source documents and their parsing/extraction state for evidence planning."),
            Read("list_individuals",
                "Search and paginate ABox individuals, optionally restricted to a class."),
            Read("list_releases",
                "List immutable release drafts, published versions, and deployment state."),
            Read("list_review_items",
                "List conflict, entity-resolution, terminology, or validation review items."),
            Read("list_vocabulary_concepts",
                "Browse and search SKOS concepts in the mutable workspace vocabulary."),
            Destructive("manage_release",
                "Create, review, publish, roll back, deploy, stop, or delete an immutable release.",
                McpTokenScopes.McpManage),
            Read("preview_ontology_changes",
                "Validate a structured ontology change set and return its exact RDF diff without saving it."),
            Read("query_knowledge",
                "Run bounded read-only SPARQL SELECT or ASK over workspace TBox, ABox, and SKOS."),
            Read("resolve_term",
                "Resolve a preferred, alternative, or hidden SKOS label to controlled concepts."),
            Destructive("rollback_history_event",
                "Reverse one audited workspace event. This is an owner-confirmed destructive action.",
                McpTokenScopes.McpManage),
            Read("search_ontology",
                "Search TBox classes and properties by label, IRI, or description."),
            ReadWrite("start_extraction",
                "Start TBox, ABox, or combined extraction for selected source chunks.",
                McpTokenScopes.McpWrite),
        };
        tools.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return tools;
    }

    private static McpToolDescriptor Read(string name, string description, params string[] scopes) =>
        new(name, description, scopes.Length == 0 ? new[] { McpTokenScopes.McpRead } : scopes);

    private static McpToolDescriptor ReadWrite(string name, string description, params string[] scopes) =>
        new(name, description, scopes);

    private static McpToolDescriptor Destructive(string name, string description, params string[] scopes) =>
        new(name, description, scopes);

    // -----------------------------------------------------------------
    // Tool methods — every method is decorated with
    // [McpServerTool(Name = "...", Destructive = false/true)] so the
    // SDK picks it up on reflection. The bodies are intentionally thin:
    // they resolve the principal, enforce scope + role, and delegate
    // to IIntegrationApiFacade.InvokeAsync so the dispatcher remains
    // the single source of truth for the operation mapping.
    // -----------------------------------------------------------------

    /// <summary>
    /// Create/delete an individual or add/remove an ABox assertion in the
    /// mutable workspace.
    /// </summary>
    [McpServerTool(Name = "apply_instance_change", Destructive = true, ReadOnly = false)]
    public async Task<object> Apply_instance_change(
        HttpContext httpContext,
        string action,
        IDictionary<string, object?> payload,
        bool confirm_destructive,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm_destructive);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpWrite);
        await _accessor.RequireRoleAsync(principal, KSRole.Editor, cancellationToken).ConfigureAwait(false);
        EnforceDestructiveCaps(payload);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: ToBody(action, payload),
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(MapAction("abox", action), request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>
    /// Atomically apply a previewed TBox change set to the mutable
    /// workspace and audit it.
    /// </summary>
    [McpServerTool(Name = "apply_ontology_changes", Destructive = true, ReadOnly = false)]
    public async Task<object> Apply_ontology_changes(
        HttpContext httpContext,
        IEnumerable<IDictionary<string, object?>> operations,
        bool confirm_destructive,
        string? reason,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm_destructive);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpWrite);
        await _accessor.RequireRoleAsync(principal, KSRole.Editor, cancellationToken).ConfigureAwait(false);

        var ops = operations as IList<IDictionary<string, object?>> ?? operations.ToList();
        EnforceDestructiveCaps(ops);
        var body = new Dictionary<string, object?>
        {
            ["operations"] = ops,
            ["reason"] = reason ?? string.Empty,
            ["confirm_destructive"] = true,
        };
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: body,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("ontology.edit", request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>
    /// Create, update, delete, or synchronize SKOS schemes and concepts.
    /// </summary>
    [McpServerTool(Name = "apply_vocabulary_change", Destructive = true, ReadOnly = false)]
    public async Task<object> Apply_vocabulary_change(
        HttpContext httpContext,
        string action,
        bool confirm_destructive,
        string? iri,
        IDictionary<string, object?>? payload,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm_destructive);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpWrite);
        await _accessor.RequireRoleAsync(principal, KSRole.Editor, cancellationToken).ConfigureAwait(false);

        var body = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["confirm_destructive"] = true,
        };
        if (iri is not null) body["iri"] = iri;
        if (payload is not null) body["payload"] = payload;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: body,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(MapAction("vocabulary", action), request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>
    /// Resolve or dismiss a governance queue item using the same audited
    /// application services.
    /// </summary>
    [McpServerTool(Name = "decide_review_item", Destructive = true, ReadOnly = false)]
    public async Task<object> Decide_review_item(
        HttpContext httpContext,
        string queue,
        int item_id,
        string action,
        bool confirm,
        IDictionary<string, object?>? payload,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpWrite);
        await _accessor.RequireRoleAsync(principal, KSRole.Editor, cancellationToken).ConfigureAwait(false);

        var body = new Dictionary<string, object?>
        {
            ["queue"] = queue,
            ["item_id"] = item_id,
            ["action"] = action,
            ["confirm"] = true,
        };
        if (payload is not null) body["payload"] = payload;
        var operation = MapReviewQueue(queue, action);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: item_id.ToString(),
            SecondResourceId: null,
            Body: body,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(operation, request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>Read the audited change history for the bound knowledge system.</summary>
    [McpServerTool(Name = "get_history", Destructive = false, ReadOnly = true)]
    public async Task<object> Get_history(
        HttpContext httpContext,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: new Dictionary<string, string?>
            {
                ["limit"] = limit.ToString(),
                ["offset"] = offset.ToString(),
            },
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("history.get", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>() };
    }

    /// <summary>Read one individual with types, assertions, and source evidence.</summary>
    [McpServerTool(Name = "get_individual", Destructive = false, ReadOnly = true)]
    public async Task<object> Get_individual(
        HttpContext httpContext,
        string iri,
        CancellationToken cancellationToken)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: iri,
            SecondResourceId: null,
            Body: null,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("abox.get_individual", request, cancellationToken).ConfigureAwait(false)
            ?? new { iri = iri, type_iris = Array.Empty<string>() };
    }

    /// <summary>Read the current mutable TBox as structured classes, properties, axioms, and labels.</summary>
    [McpServerTool(Name = "get_ontology", Destructive = false, ReadOnly = true)]
    public async Task<object> Get_ontology(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var actor = new Actor(principal.User.Id.ToString());
        var response = await _facade.GetOntologyAsync(1L, actor, cancellationToken).ConfigureAwait(false);
        return new
        {
            classes = response.Classes,
            properties = response.Properties,
        };
    }

    /// <summary>Get the bound workspace, current user role, graph statistics, and governance blockers.</summary>
    [McpServerTool(Name = "get_workspace_context", Destructive = false, ReadOnly = true)]
    public async Task<object> Get_workspace_context(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var role = await _accessor.GetEffectiveRoleAsync(principal, cancellationToken).ConfigureAwait(false);
        return new
        {
            workspace_id = principal.KnowledgeSystem.PublicId,
            user_id = principal.User.Id,
            role = role.ToString().ToLowerInvariant(),
            scopes = principal.Scopes,
            ks_id = 1L,
        };
    }

    /// <summary>List source documents and their parsing/extraction state for evidence planning.</summary>
    [McpServerTool(Name = "list_documents", Destructive = false, ReadOnly = true)]
    public async Task<object> List_documents(
        HttpContext httpContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: new Dictionary<string, string?>
            {
                ["limit"] = limit.ToString(),
                ["offset"] = offset.ToString(),
            },
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("documents.list", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>(), total = 0 };
    }

    /// <summary>Search and paginate ABox individuals, optionally restricted to a class.</summary>
    [McpServerTool(Name = "list_individuals", Destructive = false, ReadOnly = true)]
    public async Task<object> List_individuals(
        HttpContext httpContext,
        int limit = 20,
        int offset = 0,
        string? class_iri = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var qs = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(),
            ["offset"] = offset.ToString(),
        };
        if (class_iri is not null) qs["class_iri"] = class_iri;
        if (query is not null) qs["query"] = query;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: qs,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("abox.list_individuals", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>(), total = 0 };
    }

    /// <summary>List immutable release drafts, published versions, and deployment state.</summary>
    [McpServerTool(Name = "list_releases", Destructive = false, ReadOnly = true)]
    public async Task<object> List_releases(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("releases.list", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>(), total = 0 };
    }

    /// <summary>List conflict, entity-resolution, terminology, or validation review items.</summary>
    [McpServerTool(Name = "list_review_items", Destructive = false, ReadOnly = true)]
    public async Task<object> List_review_items(
        HttpContext httpContext,
        string queue,
        int limit = 50,
        int offset = 0,
        string? query = null,
        string status = "all",
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var operation = queue switch
        {
            "conflicts" => "conflicts.list",
            "resolution" => "resolution.get_queue",
            "validation" => "abox.list_validation_decisions",
            "terminology" => "vocabulary.list_proposals",
            _ => "conflicts.list",
        };
        var qs = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(),
            ["offset"] = offset.ToString(),
            ["status"] = status,
        };
        if (query is not null) qs["query"] = query;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: queue,
            SecondResourceId: null,
            Body: null,
            Query: qs,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(operation, request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>(), total = 0 };
    }

    /// <summary>Browse and search SKOS concepts in the mutable workspace vocabulary.</summary>
    [McpServerTool(Name = "list_vocabulary_concepts", Destructive = false, ReadOnly = true)]
    public async Task<object> List_vocabulary_concepts(
        HttpContext httpContext,
        int limit = 100,
        int offset = 0,
        string? query = null,
        string? scheme_iri = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var qs = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(),
            ["offset"] = offset.ToString(),
        };
        if (query is not null) qs["query"] = query;
        if (scheme_iri is not null) qs["scheme_iri"] = scheme_iri;
        if (status is not null) qs["status"] = status;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: qs,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("vocabulary.list_concepts", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>(), total = 0 };
    }

    /// <summary>Create, review, publish, roll back, deploy, stop, or delete an immutable release.</summary>
    [McpServerTool(Name = "manage_release", Destructive = true, ReadOnly = false)]
    public async Task<object> Manage_release(
        HttpContext httpContext,
        string action,
        bool confirm,
        IDictionary<string, object?>? payload,
        int? release_id,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpManage);
        await _accessor.RequireRoleAsync(principal, KSRole.Owner, cancellationToken).ConfigureAwait(false);

        var body = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["confirm"] = true,
        };
        if (payload is not null) body["payload"] = payload;
        if (release_id is not null) body["release_id"] = release_id;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: release_id?.ToString(),
            SecondResourceId: null,
            Body: body,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(MapAction("releases", action), request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>
    /// Validate a structured ontology change set and return its exact RDF
    /// diff without saving it. Preview must not mutate state.
    /// </summary>
    [McpServerTool(Name = "preview_ontology_changes", Destructive = false, ReadOnly = true)]
    public async Task<object> Preview_ontology_changes(
        HttpContext httpContext,
        IEnumerable<IDictionary<string, object?>> operations,
        CancellationToken cancellationToken)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var ops = operations as IList<IDictionary<string, object?>> ?? operations.ToList();
        var edits = ops.Select(BuildEditOperation).ToList();
        var actor = new Actor(principal.User.Id.ToString());
        var preview = await _facade.PreviewOntologyChangesAsync(1L, edits, actor, cancellationToken).ConfigureAwait(false);
        return new
        {
            added_triples = preview.AddedTriples,
            removed_triples = preview.RemovedTriples,
            operation_count = ops.Count,
        };
    }

    /// <summary>Run bounded read-only SPARQL SELECT or ASK over workspace TBox, ABox, and SKOS.</summary>
    [McpServerTool(Name = "query_knowledge", Destructive = false, ReadOnly = true)]
    public async Task<object> Query_knowledge(
        HttpContext httpContext,
        string sparql,
        int max_rows = 100,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var token = new TokenPrincipal(
            TokenId: principal.User.Id.ToString(),
            KnowledgeSystemPublicId: principal.KnowledgeSystem.PublicId,
            Scopes: principal.Scopes);
        var response = await _facade.QueryAsync(
            principal.KnowledgeSystem.PublicId, sparql, max_rows, token, cancellationToken).ConfigureAwait(false);
        return new { rows = response.Rows };
    }

    /// <summary>Resolve a preferred, alternative, or hidden SKOS label to controlled concepts.</summary>
    [McpServerTool(Name = "resolve_term", Destructive = false, ReadOnly = true)]
    public async Task<object> Resolve_term(
        HttpContext httpContext,
        string query,
        string? language = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var qs = new Dictionary<string, string?>
        {
            ["query"] = query,
            ["limit"] = limit.ToString(),
        };
        if (language is not null) qs["language"] = language;
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: qs,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("vocabulary.resolve_term", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>() };
    }

    /// <summary>Reverse one audited workspace event. Owner-confirmed destructive action.</summary>
    [McpServerTool(Name = "rollback_history_event", Destructive = true, ReadOnly = false)]
    public async Task<object> Rollback_history_event(
        HttpContext httpContext,
        int event_id,
        bool confirm,
        CancellationToken cancellationToken)
    {
        EnsureDestructiveConfirmed(confirm);
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpManage);
        await _accessor.RequireRoleAsync(principal, KSRole.Owner, cancellationToken).ConfigureAwait(false);

        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: event_id.ToString(),
            SecondResourceId: null,
            Body: new Dictionary<string, object?> { ["confirm"] = true },
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("history.rollback", request, cancellationToken).ConfigureAwait(false)
            ?? new { ok = true };
    }

    /// <summary>Search TBox classes and properties by label, IRI, or description.</summary>
    [McpServerTool(Name = "search_ontology", Destructive = false, ReadOnly = true)]
    public async Task<object> Search_ontology(
        HttpContext httpContext,
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpRead);
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: null,
            Query: new Dictionary<string, string?>
            {
                ["query"] = query,
                ["limit"] = limit.ToString(),
            },
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync("ontology.search", request, cancellationToken).ConfigureAwait(false)
            ?? new { items = Array.Empty<object>() };
    }

    /// <summary>Start TBox, ABox, or combined extraction for selected source chunks.</summary>
    [McpServerTool(Name = "start_extraction", Destructive = false, ReadOnly = false)]
    public async Task<object> Start_extraction(
        HttpContext httpContext,
        string mode,
        IEnumerable<int> chunk_ids,
        bool? agentic_resolution = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var principal = await _accessor.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false);
        _accessor.RequireScope(principal, McpTokenScopes.McpWrite);
        await _accessor.RequireRoleAsync(principal, KSRole.Editor, cancellationToken).ConfigureAwait(false);

        var body = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["chunk_ids"] = chunk_ids.ToList(),
        };
        if (agentic_resolution is not null) body["agentic_resolution"] = agentic_resolution.Value;
        if (model is not null) body["model"] = model;

        var operation = mode switch
        {
            "tbox" => "extraction.run",
            "abox" => "extraction.run_instances",
            "combined" => "extraction.run_combined",
            _ => "extraction.run",
        };
        var request = new InternalRequest(
            KnowledgeSystemId: 1L,
            PublicId: null,
            ResourceId: null,
            SecondResourceId: null,
            Body: body,
            Query: null,
            Actor: new Actor(principal.User.Id.ToString()));
        return await _facade.InvokeAsync(operation, request, cancellationToken).ConfigureAwait(false)
            ?? new { id = Guid.Empty, status = "queued" };
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Refuse destructive calls when the caller has not flipped the
    /// confirm flag. The wording is what the API-mcp plan pins the
    /// test assertions against.
    /// </summary>
    private static void EnsureDestructiveConfirmed(bool confirm)
    {
        if (!confirm)
        {
            throw new McpToolException(
                "Destructive operations require the confirm_destructive flag.");
        }
    }

    /// <summary>
    /// Enforce the 50-edit / 200 KiB cap on destructive payloads. The
    /// SDK surfaces the exception via the JSON-RPC <c>isError</c>
    /// path so the LLM client can ask the operator to chunk the change
    /// set.
    /// </summary>
    private static void EnforceDestructiveCaps(IDictionary<string, object?>? payload)
    {
        if (payload is null) return;
        EnforceDestructiveCaps(payload.Count, EstimateBytes(payload));
    }

    private static void EnforceDestructiveCaps(IList<IDictionary<string, object?>> operations)
    {
        if (operations.Count > MaxEditsPerDestructiveCall)
        {
            throw new McpToolException(
                $"At most {MaxEditsPerDestructiveCall} edits per destructive call; received {operations.Count}.");
        }
        var bytes = operations.Sum(EstimateBytes);
        if (bytes > MaxDestructivePayloadBytes)
        {
            throw new McpToolException(
                $"Destructive payload exceeds {MaxDestructivePayloadBytes} bytes (got {bytes}).");
        }
    }

    private static void EnforceDestructiveCaps(int count, int bytes)
    {
        if (count > MaxEditsPerDestructiveCall)
        {
            throw new McpToolException(
                $"At most {MaxEditsPerDestructiveCall} edits per destructive call; received {count}.");
        }
        if (bytes > MaxDestructivePayloadBytes)
        {
            throw new McpToolException(
                $"Destructive payload exceeds {MaxDestructivePayloadBytes} bytes (got {bytes}).");
        }
    }

    /// <summary>
    /// Cheap upper-bound estimate of the payload size: JSON-stringify
    /// each key + value pair and sum the lengths. We avoid a full
    /// serializer round-trip so the cap check stays O(n) over the
    /// payload shape.
    /// </summary>
    private static int EstimateBytes(IDictionary<string, object?> payload)
    {
        var total = 0;
        foreach (var kv in payload)
        {
            total += kv.Key?.Length ?? 0;
            total += EstimateValueBytes(kv.Value);
        }
        return total;
    }

    private static int EstimateValueBytes(object? value)
    {
        if (value is null) return 4; // "null"
        if (value is string s) return s.Length + 2;
        if (value is bool b) return b ? 4 : 5;
        if (value is int or long or short) return 20;
        if (value is double or float or decimal) return 32;
        if (value is System.Collections.IEnumerable enumerable)
        {
            var sum = 0;
            foreach (var item in enumerable)
            {
                sum += EstimateValueBytes(item);
                sum += 1; // comma separator
            }
            return sum + 2; // brackets
        }
        return value.ToString()?.Length ?? 0;
    }

    /// <summary>Convert a (verb, payload) pair to an InternalRequest body.</summary>
    private static IReadOnlyDictionary<string, object?> ToBody(string action, IDictionary<string, object?> payload)
    {
        var dict = new Dictionary<string, object?>(payload.Count + 1)
        {
            ["action"] = action,
        };
        foreach (var kv in payload)
        {
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    /// <summary>
    /// Map a free-form <c>action</c> string onto the canonical internal
    /// operation the dispatcher understands. Falls back to the verb as
    /// the suffix when the action is not in the known set.
    /// </summary>
    private static string MapAction(string domain, string action) =>
        $"{domain}.{action}";

    /// <summary>
    /// Map a (queue, action) pair onto the canonical internal operation
    /// the dispatcher understands.
    /// </summary>
    private static string MapReviewQueue(string queue, string action) =>
        queue switch
        {
            "conflicts" => $"conflicts.{action}",
            "resolution" => $"resolution.{action}",
            "validation" => $"abox.{action}",
            "terminology" => $"vocabulary.{action}",
            _ => $"{queue}.{action}",
        };

    /// <summary>
    /// Translate one operation dictionary into an
    /// <see cref="EditOperation"/>. The preview path is the only tool
    /// that consumes the field shape, so the rest of the tool surface
    /// keeps the raw dictionaries.
    /// </summary>
    private static EditOperation BuildEditOperation(IDictionary<string, object?> op)
    {
        var verb = op.TryGetValue("verb", out var verbObj) ? verbObj?.ToString() ?? string.Empty
            : op.TryGetValue("op", out var opObj) ? opObj?.ToString() ?? string.Empty
            : string.Empty;
        var target = op.TryGetValue("target", out var targetObj) ? targetObj?.ToString() ?? string.Empty
            : op.TryGetValue("iri", out var iriObj) ? iriObj?.ToString() ?? string.Empty
            : string.Empty;
        IReadOnlyDictionary<string, string>? fields = null;
        if (op.TryGetValue("fields", out var fieldsObj) && fieldsObj is IDictionary<string, object?> fobj)
        {
            fields = fobj.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
        }
        return new EditOperation(verb, target, fields);
    }
}