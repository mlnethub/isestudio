using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OnToPilot.ApiContract.Tests.Baseline;

/// <summary>
/// Lightweight HTTP harness used by the internal contract theory test.
/// Wraps <see cref="ApiContractWebApplicationFactory"/> with the bare
/// minimum scaffolding to issue one authenticated request against one
/// internal operation. The factory's test authentication handler
/// (<see cref="ApiContractWebApplicationFactory.TestSchemeName"/>) makes
/// every request carry an admin identity, so this harness does not need
/// to log in via the production session handler.
/// </summary>
internal static class ApiContractScenario
{
    /// <summary>
    /// Issue the operation and return the resulting <see cref="HttpResponseMessage"/>.
    /// The factory is created per-call so test cases cannot leak
    /// processes or seeded state into each other.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        OperationCase operation,
        CancellationToken cancellationToken = default)
    {
        using var factory = new ApiContractWebApplicationFactory();
        var client = factory.CreateClient();

        using var request = BuildRequest(operation);
        // Attach the test scheme's bearer token so the auth pipeline picks it
        // up. The scheme accepts any value, so a constant placeholder works.
        request.Headers.Add("Authorization", $"ContractTest {Guid.NewGuid():N}");
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildRequest(OperationCase operation)
    {
        // Internal paths embed placeholders like {ks_id}; the test
        // substitutes a fixed id so the controller matches the route and
        // executes the dispatcher.
        var path = operation.Path
            .Replace("{ks_id}", "1")
            .Replace("{public_id}", "demo")
            .Replace("{publicId}", "demo")
            .Replace("{document_id}", Guid.NewGuid().ToString("N"))
            .Replace("{cid}", Guid.NewGuid().ToString("N"))
            .Replace("{did}", Guid.NewGuid().ToString("N"))
            .Replace("{event_id}", Guid.NewGuid().ToString("N"))
            .Replace("{job_id}", Guid.NewGuid().ToString("N"))
            .Replace("{release_id}", Guid.NewGuid().ToString("N"))
            .Replace("{res_id}", Guid.NewGuid().ToString("N"))
            .Replace("{rid}", Guid.NewGuid().ToString("N"))
            .Replace("{token_id}", Guid.NewGuid().ToString("N"))
            .Replace("{pid}", Guid.NewGuid().ToString("N"))
            .Replace("{prompt_key}", "default")
            .Replace("{proposal_id}", Guid.NewGuid().ToString("N"))
            .Replace("{uid}", Guid.NewGuid().ToString("N"))
            .Replace("{user_id}", Guid.NewGuid().ToString("N"))
            .Replace("{filename}", "export.nq")
            .Replace("{version}", "v1");

        var method = new HttpMethod(operation.Method);
        var request = new HttpRequestMessage(method, path);

        if (method == HttpMethod.Post
            || method == HttpMethod.Put
            || method == HttpMethod.Patch
            || method.Method == "DELETE")
        {
            // Many POST/PATCH/DELETE endpoints accept an optional body;
            // we ship an empty JSON object so the dispatcher doesn't see
            // a null body.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }
}