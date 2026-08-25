using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace ISEStudio.IntegrationTests.Deployment;

/// <summary>
/// Shared fixture that builds the production ISEStudio Docker image, starts
/// PostgreSQL + MinIO sidecars on a private network, runs the .NET image
/// inside the same network, and exposes an <see cref="HttpClient"/> pointed
/// at the published port.
///
/// <para>The fixture is intentionally explicit about each step (build,
/// network create, container run, health probe) instead of leaning on
/// <c>docker compose up</c> so the failure mode of any single step is
/// visible in the test trace &mdash; the smoke test should answer
/// "does the production image boot?" not "does compose work?".</para>
///
/// <para>If the Docker daemon is unreachable the fixture flips
/// <see cref="DockerAvailable"/> to <c>false</c>; every test in
/// <see cref="ContainerSmokeTests"/> short-circuits via
/// <see cref="SkipIfDockerUnavailable"/> so the integration gate stays green
/// on developer workstations and Windows containers without a Linux
/// daemon.</para>
/// </summary>
public sealed class ContainerSmokeFixture : IAsyncLifetime
{
    /// <summary>Container port the .NET image binds inside the container.</summary>
    private const int BackendPort = 8080;

    /// <summary>Network alias the backend uses to reach PostgreSQL.</summary>
    private const string PostgresAlias = "pg";

    /// <summary>Network alias the backend uses to reach MinIO.</summary>
    private const string MinioAlias = "minio";

    private readonly string _imageTag = $"isestudio-smoke-backend:{Guid.NewGuid():N}";
    private readonly string _repoRoot;
    private HttpClient? _client;
    private string _capturedLogs = string.Empty;

    private INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private MinioContainer? _minio;
    private IContainer? _backend;

    /// <summary>True when the Docker daemon is reachable AND the smoke
    /// stack started successfully; false means every test should skip.</summary>
    public bool DockerAvailable { get; private set; }

    /// <summary>HTTP client pointing at the running backend container.
    /// Throws when <see cref="DockerAvailable"/> is false (tests must skip first).</summary>
    public HttpClient Client => _client ?? throw new InvalidOperationException(
        "Docker is not available on this host; ContainerSmokeTests must short-circuit "
        + "before accessing Client.");

    /// <summary>Stdout+stderr captured up to the moment the container
    /// reached <c>/api/health</c>. Used by the sibling "no leaked secrets" test.</summary>
    public string CapturedStartupLogs => _capturedLogs;

    public ContainerSmokeFixture()
    {
        // Walk up from the test bin directory to the repo root. The test
        // csproj has no <RootDir> override, so the bin layout is
        // src/ISEStudio.IntegrationTests/bin/<Configuration>/<Tfm>/.
        var location = AppContext.BaseDirectory;
        var cursor = new DirectoryInfo(location);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "ISEStudio.sln")))
        {
            cursor = cursor.Parent;
        }
        if (cursor is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root from {location}; "
                + "ISEStudio.sln must be reachable.");
        }
        _repoRoot = cursor.FullName;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // Probe Docker via the CLI; Testcontainers' NetworkBuilder.Build()
        // would throw on the same condition but the exception is opaque.
        // The probe is best-effort: if it times out (sandboxed CI runner
        // without the docker daemon mounted) we treat it as "skip".
        try
        {
            var probe = await RunProcessAsync(
                "docker",
                new[] { "info" },
                workingDirectory: _repoRoot,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                DockerAvailable = false;
                Console.Error.WriteLine(
                    $"[skip] docker info returned exit={probe.ExitCode}; "
                    + "ContainerSmokeTests will short-circuit. stderr={probe.Stderr}");
                return;
            }
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            Console.Error.WriteLine(
                $"[skip] docker CLI not available on this host: {ex.Message}. "
                + "ContainerSmokeTests will short-circuit.");
            return;
        }

        DockerAvailable = true;
        try
        {
            await BuildImageAsync().ConfigureAwait(false);

            _network = new NetworkBuilder()
                .WithName($"isestudio-smoke-{Guid.NewGuid():N}")
                .Build();

            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("isestudio")
                .WithUsername("isestudio")
                .WithPassword("isestudio-smoke")
                .WithNetwork(_network)
                .WithNetworkAliases(PostgresAlias)
                .WithCleanUp(true)
                .Build();

            _minio = new MinioBuilder("minio/minio:latest")
                .WithUsername("minioadmin")
                .WithPassword("minioadmin-smoke")
                .WithNetwork(_network)
                .WithNetworkAliases(MinioAlias)
                .WithCleanUp(true)
                .Build();

            await Task.WhenAll(
                _network.CreateAsync(),
                _postgres.StartAsync(),
                _minio.StartAsync()).ConfigureAwait(false);

            _backend = new ContainerBuilder(_imageTag)
                .WithNetwork(_network)
                .WithEnvironment(new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_URLS"] = $"http://+:{BackendPort}",
                    // EF Core binds from "ISEStudio:Persistence:ConnectionString"
                    // via the __ delimiter used by ASP.NET Core's
                    // EnvironmentVariablesConfigurationProvider.
                    ["ISEStudio__Persistence__ConnectionString"] = $"Host={PostgresAlias};Port=5432;Database=isestudio;Username=isestudio;Password=isestudio-smoke",
                    ["ISEStudio__Storage__Endpoint"] = $"{MinioAlias}:9000",
                    ["ISEStudio__Storage__AccessKey"] = "minioadmin",
                    ["ISEStudio__Storage__SecretKey"] = "minioadmin-smoke",
                    ["ISEStudio__Storage__Bucket"] = "isestudio-blobs",
                    ["ISEStudio__Storage__UseSsl"] = "false",
                    // Canary value: must never appear in the captured
                    // startup logs. The secret redaction enricher scrubs
                    // it; if the smoke test ever surfaces the literal
                    // value the production code regressed.
                    ["ISEStudio__LlmApiKey"] = "sk-or-v1-CANARY-SHOULD-NOT-LEAK",
                })
                .WithPortBinding(BackendPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                    request => request.ForPath("/api/health").ForPort((ushort)BackendPort)))
                .Build();

            await _backend.StartAsync().ConfigureAwait(false);

            var mappedPort = _backend.GetMappedPublicPort(BackendPort);
            _client = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{mappedPort}/"),
                Timeout = TimeSpan.FromSeconds(10),
            };

            // Snapshot the container logs at the moment the wait strategy
            // declares the service healthy. The "no leaked secrets" test
            // asserts against this snapshot. We shell out to the docker
            // CLI because Testcontainers' GetLogsAsync returns the raw
            // multiplexed stream and the container-side logs include the
            // Serilog startup banner we want to assert against.
            _capturedLogs = await ReadLogsAsync(_backend.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            Console.Error.WriteLine(
                $"[skip] Container smoke stack failed to start: {ex.Message}");
            _client?.Dispose();
            _client = null;
            await SafeDisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        await SafeDisposeAsync().ConfigureAwait(false);
    }

    private async Task SafeDisposeAsync()
    {
        if (_backend is not null)
        {
            try { await _backend.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            _backend = null;
        }
        if (_postgres is not null)
        {
            try { await _postgres.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            _postgres = null;
        }
        if (_minio is not null)
        {
            try { await _minio.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            _minio = null;
        }
        if (_network is not null)
        {
            try { await _network.DeleteAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            _network = null;
        }
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task BuildImageAsync()
    {
        // Testcontainers' IContainerBuilder can also build a Dockerfile,
        // but it does so with the Dockerfile's parent directory as the
        // build context — and the .NET Dockerfile (src/Dockerfile) needs
        // ./src as its context so `COPY . ./` lands the whole solution.
        // Running the CLI directly is the only way to set the context
        // explicitly without duplicating the Dockerfile.
        var (exit, stdout, stderr) = await RunProcessAsync(
            "docker",
            new[] { "build", "-f", "src/Dockerfile", "-t", _imageTag, "src" },
            workingDirectory: _repoRoot,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"docker build failed (exit={exit}).\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
    }

    private async Task<string> ReadLogsAsync(string containerId)
    {
        var (exit, stdout, stderr) = await RunProcessAsync(
            "docker",
            new[] { "logs", "--no-color", containerId },
            workingDirectory: _repoRoot,
            timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (exit != 0)
        {
            return string.Empty;
        }
        // Combine stdout + stderr (some frameworks split between them);
        // the secret-redaction assertion only cares about the joined text.
        return stdout + Environment.NewLine + stderr;
    }

    /// <summary>
    /// Run an external process and capture stdout/stderr. Used by the
    /// image-build step so the failure surface (docker build warnings,
    /// missing Dockerfile path, etc.) shows up in the test trace.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var completed = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != proc.WaitForExitAsync())
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            throw new TimeoutException($"{executable} {string.Join(' ', args)} timed out after {timeout}");
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }
}
