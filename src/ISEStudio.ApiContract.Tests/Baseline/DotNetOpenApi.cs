using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ISEStudio.ApiContract.Tests.Baseline;

/// <summary>
/// Boots the real ASP.NET Core pipeline through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> and asks it for its
/// OpenAPI document. The factory reuses
/// <see cref="ISEStudio.Program"/> so the inventory reflects every
/// controller the running app would actually expose.
///
/// Until task 2 wires the controllers and registers
/// <c>builder.Services.AddOpenApi()</c> + <c>app.MapOpenApi()</c>, the
/// document is empty. The inventory test is expected to fail loudly in
/// that state — that is the point of task 1.
/// </summary>
public static class DotNetOpenApi
{
    /// <summary>
    /// Spin up the ISEStudio app, request its OpenAPI document, and
    /// flatten it to <see cref="OpenApiOperation"/> records. The factory
    /// is disposed on every call so tests cannot leak processes between
    /// runs.
    /// </summary>
    public static IReadOnlyList<OpenApiOperation> ReadOperations()
    {
        using var factory = new ApiContractWebApplicationFactory();
        var client = factory.CreateClient();

        // ASP.NET Core 10's Microsoft.AspNetCore.OpenApi package
        // exposes the generated document at /openapi/v1.json once
        // AddOpenApi() + MapOpenApi() are wired. Until then this call
        // 404s and we fall back to an empty inventory.
        using var response = client.GetAsync("/openapi/v1.json").GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<OpenApiOperation>();
        }

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseDocument(body);
    }

    private static IReadOnlyList<OpenApiOperation> ParseDocument(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("paths", out var paths))
        {
            return Array.Empty<OpenApiOperation>();
        }

        var operations = new List<OpenApiOperation>();
        foreach (var pathElement in paths.EnumerateObject())
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

                operations.Add(new OpenApiOperation(
                    Method: verb.ToUpperInvariant(),
                    Path: route,
                    OperationId: operationId,
                    Tags: tags,
                    ExpectedStatus: BaselineHttpMethods.FirstSuccessStatus(operation)));
            }
        }

        operations.Sort((left, right) =>
        {
            var byMethod = string.CompareOrdinal(left.Method, right.Method);
            return byMethod != 0 ? byMethod : string.CompareOrdinal(left.Path, right.Path);
        });
        return operations;
    }
}

internal static class BaselineHttpMethods
{
    public static bool IsHttpMethod(string name) =>
        name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options";

    public static int FirstSuccessStatus(JsonElement operation)
    {
        if (operation.TryGetProperty("responses", out var responses))
        {
            foreach (var status in responses.EnumerateObject())
            {
                if (status.Name.StartsWith("2", StringComparison.Ordinal)
                    && int.TryParse(status.Name, out var code))
                {
                    return code;
                }
            }
        }

        return 200;
    }
}