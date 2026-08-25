using System.Net;

namespace ISEStudio.IntegrationTests.Deployment;

/// <summary>
/// Smoke tests that prove the production Docker image is bootable end-to-end.
/// <see cref="ContainerSmokeFixture"/> builds the image with
/// <c>docker build -f src/Dockerfile src</c>, starts PostgreSQL + MinIO
/// sidecars on a private network, runs the image, and exposes an
/// <see cref="System.Net.Http.HttpClient"/> pointed at the published port.
///
/// <para>All tests in this class share the same fixture instance (via
/// <see cref="IClassFixture{TFixture}"/>); the first test pays the build /
/// boot latency and the others reuse the warm container. The class is
/// tagged <c>[Trait("Category", "Container")]</c> so CI shards that only
/// want the unit + contract surface can filter it out with
/// <c>--filter Category!=Container</c>.</para>
///
/// <para>If Docker isn't available on the host (developer workstation,
/// Windows container without the Linux engine) the fixture flips
/// <see cref="ContainerSmokeFixture.DockerAvailable"/> to <c>false</c> and
/// every test short-circuits via <see cref="SkipIfDockerUnavailable"/> so
/// the integration gate stays green.</para>
/// </summary>
[Trait("Category", "Container")]
public sealed class ContainerSmokeTests : IClassFixture<ContainerSmokeFixture>
{
    private readonly ContainerSmokeFixture _fixture;

    /// <summary>
    /// Shortcut to the fixture's HttpClient so the verbatim required
    /// test reads identically to the plan/brief.
    /// </summary>
    private System.Net.Http.HttpClient Client => _fixture.Client;

    public ContainerSmokeTests(ContainerSmokeFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Verbatim required test from the migration plan. Polls
    /// <c>/api/health</c> until it returns <c>200 OK</c>, or fails after
    /// a two-minute deadline (which covers NuGet restore, EF Core warmup,
    /// and the MinIO S3 handshake on a cold cache).
    /// </summary>
    [Fact]
    public async Task Production_container_becomes_healthy_on_api_health()
    {
        if (SkipIfDockerUnavailable()) return;
        var response = await Retry.UntilSuccessAsync(() => Client.GetAsync("/api/health"), TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Sibling test: once the container is healthy the response body
    /// must carry the same <c>status</c> field the Python backend emits
    /// so the existing <c>/api/health</c> consumers (load balancers,
    /// uptime probes) don't need to special-case the .NET rollout.
    /// </summary>
    [Fact]
    public async Task Health_endpoint_returns_200_with_status_field()
    {
        if (SkipIfDockerUnavailable()) return;
        var response = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sibling test: the Serilog startup log stream MUST NOT include the
    /// <c>ISEStudio__LlmApiKey</c> canary value the fixture seeded. The
    /// secret redaction enricher (see
    /// <c>ISEStudio.Observability.SecretRedactionProcessor</c>) is the
    /// mechanism that enforces this — if it regresses the assertion
    /// fires and the smoke gate blocks the rollout.
    /// </summary>
    [Fact]
    public void Startup_logs_do_not_leak_secrets()
    {
        if (SkipIfDockerUnavailable()) return;

        var logs = _fixture.CapturedStartupLogs;
        Assert.NotEmpty(logs);

        Assert.DoesNotContain("CANARY-SHOULD-NOT-LEAK", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-v1-CANARY", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minioadmin-smoke", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isestudio-smoke", logs, StringComparison.OrdinalIgnoreCase);
    }

    private bool SkipIfDockerUnavailable()
    {
        if (_fixture.DockerAvailable) return false;
        Console.Error.WriteLine(
            "[skip] Docker is not available on this host; ContainerSmokeTests short-circuits.");
        return true;
    }
}
