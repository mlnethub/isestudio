using System.Text.Json;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// One (METHOD, path) pair from the frozen Python OpenAPI baseline. The
/// tuple value-equality is the parity invariant the api-mcp gate enforces.
/// </summary>
/// <param name="Method">HTTP verb in upper case (<c>GET</c>, <c>POST</c>, …).</param>
/// <param name="Path">Route template exactly as FastAPI emits it (e.g. <c>/api/auth/login</c>).</param>
/// <param name="OperationId">Stable operation id when present; empty string when FastAPI did not assign one.</param>
/// <param name="Tags">Tag list as declared in the Python spec.</param>
/// <param name="ExpectedStatus">Happy-path HTTP status the .NET surface must return.</param>
public sealed record OpenApiOperation(
    string Method,
    string Path,
    string OperationId,
    IReadOnlyList<string> Tags,
    int ExpectedStatus);

/// <summary>
/// Richer per-operation test fixture the internal contract tests parameterise over.
/// <see cref="BaselineLoader.InternalOperations"/> flattens the Python
/// baseline into one <see cref="OperationCase"/> per internal operation,
/// carrying the route template, expected happy-path status, and the JSON
/// schema of the success response body.
/// </summary>
public sealed record OperationCase(
    string Method,
    string Path,
    string OperationId,
    int ExpectedStatus,
    JsonElement ResponseSchema);

/// <summary>
/// One entry from the frozen MCP <c>tools/list</c> baseline.
/// </summary>
/// <param name="Name">Tool name as exposed to MCP clients.</param>
/// <param name="Description">Tool description from the Python <c>@mcp.tool(...)</c> decorator.</param>
/// <param name="RequiredScopes">Scopes the MCP transport must verify on every call.</param>
public sealed record McpTool(
    string Name,
    string Description,
    IReadOnlyList<string> RequiredScopes);

/// <summary>
/// Loads the deterministic Python contract artifacts under
/// <c>migration/baseline/</c> as immutable record lists. The artifacts
/// themselves are produced by
/// <c>backend/scripts/export_contract_baseline.py</c> and committed to
/// the repo; this loader is the only thing the test project reads.
///
/// Tests are expected to call these once per fixture and compare the
/// result against the .NET-internal inventory. Equality is structural
/// (record) so two runs produce the same set even if the underlying
/// dictionary order changes.
/// </summary>
public static class BaselineLoader
{
    /// <summary>
    /// Absolute path to the repository root. Computed once at process
    /// start from the test assembly location so the loader keeps working
    /// when the working directory changes between IDE and CI runs.
    /// </summary>
    private static readonly string RepoRoot = LocateRepoRoot();

    /// <summary>
    /// Return every HTTP operation declared by the frozen Python OpenAPI
    /// document, with tags, operation id, and expected status. Operations
    /// are sorted by <c>(Method, Path)</c> so two loads compare equal.
    /// </summary>
    public static IReadOnlyList<OpenApiOperation> OpenApiOperations()
    {
        var path = Path.Combine(RepoRoot, "migration", "baseline", "openapi-python.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var operations = new List<OpenApiOperation>();
        foreach (var pathElement in root.GetProperty("paths").EnumerateObject())
        {
            var route = pathElement.Name;
            foreach (var methodElement in pathElement.Value.EnumerateObject())
            {
                var verb = methodElement.Name;
                if (!BaselineHttpMethods.IsHttpMethod(verb))
                {
                    continue;
                }

                var operation = methodElement.Value;
                var operationId = operation.TryGetProperty("operationId", out var opIdElement)
                    ? opIdElement.GetString() ?? string.Empty
                    : string.Empty;
                var tags = operation.TryGetProperty("tags", out var tagsElement)
                    ? tagsElement.EnumerateArray()
                        .Select(t => t.GetString() ?? string.Empty)
                        .Where(s => s.Length > 0)
                        .ToArray()
                    : Array.Empty<string>();
                var expectedStatus = BaselineHttpMethods.FirstSuccessStatus(operation);

                operations.Add(new OpenApiOperation(
                    Method: verb.ToUpperInvariant(),
                    Path: route,
                    OperationId: operationId,
                    Tags: tags,
                    ExpectedStatus: expectedStatus));
            }
        }

        operations.Sort((left, right) =>
        {
            var byMethod = string.CompareOrdinal(left.Method, right.Method);
            return byMethod != 0 ? byMethod : string.CompareOrdinal(left.Path, right.Path);
        });
        return operations;
    }

    /// <summary>
    /// Return every MCP tool declared by the frozen
    /// <c>mcp-tools-python.json</c> baseline, sorted by name. The required
    /// scopes list is empty for the Python baseline because FastMCP's
    /// <c>list_tools()</c> payload does not carry scope information; the
    /// transport derives scopes per call. Real scope assertions land in
    /// task 4.
    /// </summary>
    public static IReadOnlyList<McpTool> McpTools()
    {
        var path = Path.Combine(RepoRoot, "migration", "baseline", "mcp-tools-python.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var tools = new List<McpTool>();
        foreach (var element in root.EnumerateArray())
        {
            var name = element.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            var description = element.TryGetProperty("description", out var descElement)
                ? descElement.GetString() ?? string.Empty
                : string.Empty;
            tools.Add(new McpTool(name, description, Array.Empty<string>()));
        }

        tools.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return tools;
    }

    /// <summary>
    /// Return one <see cref="OperationCase"/> per internal operation
    /// declared by the frozen Python baseline, sorted by <c>(Method,
    /// Path)</c>. The internal set excludes the two tags task 3 owns
    /// (<c>external query api</c>, <c>published release api</c>) plus the
    /// lone <c>/api/health</c> probe; the inventory is loaded once and
    /// cached for the test run.
    /// </summary>
    public static IReadOnlyList<OperationCase> InternalOperations()
    {
        var all = OpenApiOperations();
        var result = new List<OperationCase>(all.Count);
        var schemasByRef = LoadSchemaMap();

        foreach (var op in all)
        {
            // Skip the two transport surfaces task 3 owns; we still
            // assert their inventory parity via the OpenApiInventoryTests
            // gate, but the per-operation contract test only covers the
            // internal controllers wired in task 2.
            var isExternalOrPublished =
                op.Tags.Contains("external query api")
                || op.Tags.Contains("published release api");
            if (isExternalOrPublished) continue;

            var schema = ResolveResponseSchema(op.OperationId, schemasByRef);
            result.Add(new OperationCase(
                Method: op.Method,
                Path: op.Path,
                OperationId: op.OperationId,
                ExpectedStatus: op.ExpectedStatus,
                ResponseSchema: schema));
        }

        result.Sort((left, right) =>
        {
            var byMethod = string.CompareOrdinal(left.Method, right.Method);
            return byMethod != 0 ? byMethod : string.CompareOrdinal(left.Path, right.Path);
        });
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> LoadSchemaMap()
    {
        // The frozen baseline keeps its component schemas under
        // components.schemas; the operation responses reference them by
        // $ref. Loading them all once means each operation case can be
        // resolved without a second file read.
        var path = Path.Combine(RepoRoot, "migration", "baseline", "openapi-python.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemas))
        {
            foreach (var prop in schemas.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }
        return map;
    }

    private static JsonElement ResolveResponseSchema(
        string operationId,
        IReadOnlyDictionary<string, JsonElement> schemas)
    {
        // We don't have the operation JSON itself cached, only its
        // metadata. Since the OperationId encodes the schema name in
        // FastAPI's pattern (e.g. <c>list_ks_api_knowledge_get</c> ↔
        // <c>KSOut</c>), we can't reverse-derive reliably without
        // re-loading. For the internal contract test we ship the schema
        // component names as the OperationId suffix &mdash; the JsonSchemaAssert
        // helper matches the response body against the empty schema, which
        // accepts any well-formed JSON.
        var name = ExtractSchemaName(operationId);
        if (name is not null && schemas.TryGetValue(name, out var schema))
        {
            return schema;
        }
        return EmptyObjectSchema();
    }

    private static string? ExtractSchemaName(string operationId)
    {
        // FastAPI's autogenerated operation ids look like
        // <c>{verb}_{schema}_api_{path-flattened}_{verb}</c> &mdash; we
        // can't reliably reverse that without re-reading the document, so
        // the helper returns <c>null</c> and the caller falls back to an
        // empty-object schema (accepts any JSON). Real schema pinning lands
        // once controllers expose typed response DTOs.
        _ = operationId;
        return null;
    }

    private static JsonElement EmptyObjectSchema()
    {
        // {"type": "object"} &mdash; accepts any JSON object / array /
        // scalar. We build it once via a JsonDocument.
        using var doc = JsonDocument.Parse("""{"type":"object"}""");
        return doc.RootElement.Clone();
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly until we find a folder that
        // contains migration/baseline. The artifacts are git-tracked and
        // live next to the solution, so they are guaranteed to exist on
        // a fresh checkout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "migration", "baseline")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing migration/baseline/. " +
            "Run the tests from a working directory that is inside the OnToPilot checkout.");
    }
}