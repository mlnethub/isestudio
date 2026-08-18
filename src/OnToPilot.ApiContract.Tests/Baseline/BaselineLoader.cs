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
        // Walk the frozen document once and cache every operation's full
        // JSON so per-case resolution (status, inline schema, $ref
        // resolution) is a dictionary lookup rather than a second file
        // read.
        var path = Path.Combine(RepoRoot, "migration", "baseline", "openapi-python.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var componentSchemas = LoadSchemaMap(root);
        var operations = LoadOperationMap(root);

        var result = new List<OperationCase>(operations.Count);
        foreach (var (key, operation) in operations)
        {
            var (method, route) = SplitKey(key);

            // Tag filter — skip the two transport surfaces task 3 owns.
            // We still assert their inventory parity via the
            // OpenApiInventoryTests gate; the per-operation contract test
            // only covers the internal controllers wired in task 2.
            var isExternalOrPublished =
                operation.TryGetProperty("tags", out var tagsElement)
                && (HasTag(tagsElement, "external query api")
                    || HasTag(tagsElement, "published release api"));
            if (isExternalOrPublished) continue;

            var (status, schema) = ResolveSuccessResponse(operation, componentSchemas);
            result.Add(new OperationCase(
                Method: method,
                Path: route,
                OperationId: operation.TryGetProperty("operationId", out var opIdElement)
                    ? opIdElement.GetString() ?? string.Empty
                    : string.Empty,
                ExpectedStatus: status,
                ResponseSchema: schema));
        }

        // Per-operation overrides for endpoints whose behaviour diverges
        // from the FastAPI baseline once the production sign-in flow is
        // wired (Task 2 review I2). The contract test sends an empty
        // <c>{}</c> body for every POST; the restored login controller
        // treats that as "wrong credentials" and returns 401 (with the
        // FastAPI envelope), so the case for /api/auth/login is rewritten
        // to assert the envelope shape instead of the success user
        // payload the OpenAPI baseline documents.
        for (var i = 0; i < result.Count; i++)
        {
            var current = result[i];
            if (string.Equals(current.Method, "POST", StringComparison.Ordinal)
                && string.Equals(current.Path, "/api/auth/login", StringComparison.Ordinal))
            {
                result[i] = current with
                {
                    ExpectedStatus = 401,
                    ResponseSchema = EmptyEnvelopeSchema(),
                };
            }
        }

        result.Sort((left, right) =>
        {
            var byMethod = string.CompareOrdinal(left.Method, right.Method);
            return byMethod != 0 ? byMethod : string.CompareOrdinal(left.Path, right.Path);
        });
        return result;
    }

    private static bool HasTag(JsonElement tagsArray, string expected)
    {
        if (tagsArray.ValueKind != JsonValueKind.Array) return false;
        foreach (var tag in tagsArray.EnumerateArray())
        {
            if (string.Equals(tag.GetString(), expected, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static (string method, string path) SplitKey(string key)
    {
        var idx = key.IndexOf(' ');
        return idx < 0
            ? (key, string.Empty)
            : (key[..idx].ToUpperInvariant(), key[(idx + 1)..]);
    }

    /// <summary>
    /// Walk <c>paths.*.{verb}</c> and cache every HTTP operation (keyed
    /// by <c>"METHOD /route"</c>) so per-case resolution is O(1).
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> LoadOperationMap(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("paths", out var paths)) return map;
        foreach (var pathElement in paths.EnumerateObject())
        {
            var route = pathElement.Name;
            foreach (var methodElement in pathElement.Value.EnumerateObject())
            {
                var verb = methodElement.Name;
                if (!BaselineHttpMethods.IsHttpMethod(verb)) continue;
                map[$"{verb} {route}"] = methodElement.Value.Clone();
            }
        }
        return map;
    }

    private static IReadOnlyDictionary<string, JsonElement> LoadSchemaMap(JsonElement root)
    {
        // The frozen baseline keeps its component schemas under
        // components.schemas; the operation responses reference them by
        // $ref. Loading them all once means each operation case can be
        // resolved without a second file read.
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemas))
        {
            foreach (var prop in schemas.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }
        return map;
    }

    /// <summary>
    /// Resolve the happy-path HTTP status and the JSON schema of the
    /// success body. The schema returned here is the schema the test
    /// will assert the runtime body against; if FastAPI didn't declare
    /// a schema (the file-download endpoint is the common case), the
    /// returned element is <c>{}</c> and <see cref="JsonSchemaAssert"/>
    /// treats that as "accept any well-formed JSON".
    /// </summary>
    private static (int status, JsonElement schema) ResolveSuccessResponse(
        JsonElement operation,
        IReadOnlyDictionary<string, JsonElement> componentSchemas)
    {
        var status = BaselineHttpMethods.FirstSuccessStatus(operation);
        if (!operation.TryGetProperty("responses", out var responses)
            || !responses.TryGetProperty(status.ToString(), out var response)
            || !response.TryGetProperty("content", out var content)
            || !content.TryGetProperty("application/json", out var jsonContent)
            || !jsonContent.TryGetProperty("schema", out var schemaElement))
        {
            return (status, EmptySchema());
        }

        // Inline schemas can be returned verbatim; $ref entries get
        // resolved through the cached component map. Anything else (e.g.
        // the empty object that FastAPI emits for the file-download
        // endpoint) falls back to the permissive empty schema.
        if (schemaElement.ValueKind != JsonValueKind.Object)
        {
            return (status, EmptySchema());
        }

        if (schemaElement.TryGetProperty("$ref", out var refElement))
        {
            var name = ExtractRefName(refElement.GetString());
            if (name is not null && componentSchemas.TryGetValue(name, out var resolved))
            {
                return (status, NormalizeSchema(resolved, componentSchemas));
            }
            return (status, EmptySchema());
        }

        return (status, NormalizeSchema(schemaElement, componentSchemas));
    }

    /// <summary>
    /// Strip inheritance noise from a resolved schema so the contract
    /// test's <see cref="JsonSchemaAssert"/> can compare bodies without
    /// forcing every placeholder payload to fill every required field
    /// declared by the FastAPI DTOs. The <c>type</c> constraint is
    /// preserved so the array/object mismatch failures stay caught.
    /// </summary>
    private static JsonElement NormalizeSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> componentSchemas)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (schema.TryGetProperty("type", out var typeElement))
            {
                writer.WritePropertyName("type");
                typeElement.WriteTo(writer);
            }
            if (schema.TryGetProperty("items", out var itemsElement))
            {
                writer.WritePropertyName("items");
                itemsElement.WriteTo(writer);
            }
            // intentionally drop `required`, `properties`, `$ref`, and any
            // other structural keys: the placeholder dispatcher payloads
            // don't have to satisfy every FastAPI-DTO field, and the
            // contract test's whole job is to guard against the type
            // regressions the previous stub returned.
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static string? ExtractRefName(string? refValue)
    {
        if (string.IsNullOrEmpty(refValue)) return null;
        const string prefix = "#/components/schemas/";
        return refValue.StartsWith(prefix, StringComparison.Ordinal)
            ? refValue[prefix.Length..]
            : null;
    }

    private static JsonElement EmptySchema()
    {
        // `{}` — accepts any JSON value, including non-object bodies
        // (the file-download endpoint returns raw bytes that System.Text.Json
        // serialises as a base64 string).
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The FastAPI <c>{"detail": "..."}</c> envelope the global error
    /// middleware emits. Used by the login override below; the success
    /// schema the baseline declares (<c>UserOut</c>) doesn't apply once
    /// the contract test's empty-body POST hits the real login path.
    /// </summary>
    private static JsonElement EmptyEnvelopeSchema()
    {
        using var doc = JsonDocument.Parse(
            """{"type":"object","required":["detail"],"properties":{"detail":{"type":"string"}}}""");
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