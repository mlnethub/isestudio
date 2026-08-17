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