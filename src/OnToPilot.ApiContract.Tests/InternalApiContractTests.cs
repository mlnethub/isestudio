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
        Assert.Equal(operation.ExpectedStatus, (int)response.StatusCode);
        JsonSchemaAssert.Compatible(operation.ResponseSchema, await response.Content.ReadAsStringAsync());
    }
}