namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Gate that proves the .NET REST surface has the same (METHOD, path)
/// surface as the frozen Python baseline. The diff between
/// <see cref="BaselineLoader.OpenApiOperations"/> and
/// <see cref="DotNetOpenApi.ReadOperations"/> must be empty in both
/// directions. Extra or missing operations are reported with a clear
/// diff so the missing-controller list is obvious in the test runner
/// output.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class OpenApiInventoryTests
{
    /// <summary>
    /// Exact verbatim name required by the api-mcp plan. The test is
    /// expected to fail with a clear diff until task 2 wires all the
    /// internal controllers and registers the OpenAPI document.
    /// </summary>
    [Fact]
    public void Dotnet_openapi_contains_every_python_operation()
    {
        var expected = BaselineLoader.OpenApiOperations();
        var actual = DotNetOpenApi.ReadOperations();

        var missingInDotNet = expected.Except(actual).ToArray();
        var extraInDotNet = actual.Except(expected).ToArray();

        var detail = BuildDiffReport(missingInDotNet, extraInDotNet);
        Assert.True(
            missingInDotNet.Length == 0 && extraInDotNet.Length == 0,
            $"OpenAPI inventory drift between Python baseline and .NET app.\n{detail}");
    }

    private static string BuildDiffReport(
        IReadOnlyList<OpenApiOperation> missingInDotNet,
        IReadOnlyList<OpenApiOperation> extraInDotNet)
    {
        var builder = new System.Text.StringBuilder();
        if (missingInDotNet.Count > 0)
        {
            builder.AppendLine($"Operations present in Python baseline but missing in .NET ({missingInDotNet.Count}):");
            foreach (var op in missingInDotNet)
            {
                builder.AppendLine($"  - {op.Method} {op.Path}  (operationId={op.OperationId})");
            }
        }
        if (extraInDotNet.Count > 0)
        {
            builder.AppendLine($"Operations present in .NET but missing in Python baseline ({extraInDotNet.Count}):");
            foreach (var op in extraInDotNet)
            {
                builder.AppendLine($"  + {op.Method} {op.Path}  (operationId={op.OperationId})");
            }
        }
        return builder.ToString();
    }
}