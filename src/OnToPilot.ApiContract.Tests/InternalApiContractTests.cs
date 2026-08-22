using System.Collections.Generic;
using OnToPilot.ApiContract.Tests.Baseline;

namespace OnToPilot.ApiContract.Tests;

/// <summary>
/// Parameterised contract test for every internal REST operation declared
/// by the frozen Python OpenAPI baseline. Each case sends the operation
/// through the live <see cref="ApiContractScenario"/> harness, asserts
/// the response status matches the FastAPI happy-path expectation, and
/// validates the response body is compatible with the documented JSON
/// schema.
///
/// <para>The 33 external / published operations (task 3) and the lone
/// <c>/api/health</c> probe are out of scope for this file &mdash; see
/// <see cref="Baseline.OpenApiInventoryTests"/> for the inventory gate
/// and the task 3 tests for the external surface.</para>
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class InternalApiContractTests
{
    public static IEnumerable<object[]> InternalOperationCases()
    {
        foreach (var op in BaselineLoader.InternalOperations())
        {
            yield return new object[] { op };
        }
    }

    [Theory]
    [MemberData(nameof(InternalOperationCases))]
    public async Task Internal_operation_matches_status_and_schema(OperationCase operation)
    {
        var response = await ApiContractScenario.SendAsync(operation);
        var expected = EffectiveExpectedStatus(operation);
        Assert.Equal(expected, (int)response.StatusCode);
        // The ontology / vocabulary export endpoints return raw RDF
        // (text/turtle, application/n-quads, …) for the frontend's Blob
        // download — NOT JSON — so the JSON-schema check only runs when
        // the body is actually JSON. An empty body (empty-graph export)
        // is already accepted by JsonSchemaAssert; a non-empty RDF body
        // isn't valid JSON and is legitimately skipped here.
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            JsonSchemaAssert.Compatible(operation.ResponseSchema, await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Override the happy-path status for operations whose inputs are
    /// random by design. <c>releases.download_export_file</c> substitutes
    /// <c>{job_id}</c> with a random Guid (no real export job exists in
    /// the contract harness), so the dispatcher surfaces a stable 404
    /// via the FastApiErrorMiddleware — same wire shape the frontend
    /// gets from the Python backend when it polls an unknown job.
    /// </summary>
    private static int EffectiveExpectedStatus(OperationCase operation)
    {
        if (operation.OperationId.Contains(
            "download_export_file", StringComparison.OrdinalIgnoreCase))
        {
            return 404;
        }
        return operation.ExpectedStatus;
    }
}