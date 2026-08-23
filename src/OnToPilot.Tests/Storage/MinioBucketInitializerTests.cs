using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using OnToPilot.Storage;

namespace OnToPilot.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="MinioBucketInitializer"/>. These cover the
/// hosted-service contract (called exactly once at startup, error
/// surfaces as a host-startup failure, gracefully accepts the
/// <see cref="IBlobStore"/> abstraction) without needing a Docker-backed
/// MinIO — the underlying <see cref="MinioBlobStore.EnsureBucketExistsAsync"/>
/// behaviour is exercised by the integration tests in
/// <c>OnToPilot.IntegrationTests.Storage.MinioBlobStoreTests</c>.
/// </summary>
/// <remarks>
/// <para>The fake stores below subclass <see cref="MinioBlobStore"/> and
/// override the SDK-touching <c>EnsureBucketExistsAsync</c> method (which
/// requires a real <c>IAmazonS3</c> client and is impractical to fake
/// unit-side without Moq). The base ctor demands a non-null
/// <c>IAmazonS3</c>; the fakes satisfy it with a placeholder
/// <c>AmazonS3Client</c> because the overridden method never reaches
/// the SDK.</para>
/// </remarks>
public sealed class MinioBucketInitializerTests
{
    [Fact]
    [Trait("Category", "Storage")]
    public async Task StartAsync_calls_EnsureBucketExistsAsync_exactly_once()
    {
        var recording = new RecordingMinioBlobStore();
        var initializer = new MinioBucketInitializer(
            recording,
            NullLogger<MinioBucketInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, recording.EnsureCallCount);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task StartAsync_propagates_EnsureBucketExistsAsync_failure()
    {
        // The hosted service must surface a startup failure rather than
        // swallowing it: a half-broken bucket means the first upload will
        // 500, which is worse than refusing to boot. The host's
        // HostBuilder hooks translate the throw into a fatal startup log.
        var failing = new FailingMinioBlobStore();
        var initializer = new MinioBucketInitializer(
            failing,
            NullLogger<MinioBucketInitializer>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer.StartAsync(CancellationToken.None));
        Assert.Equal("simulated bucket creation failure", ex.Message);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task StopAsync_completes_without_error()
    {
        // The service has nothing to do on shutdown — the SDK client
        // disposes itself when the host tears down the service provider.
        // This test pins the no-op contract so a future contributor
        // doesn't accidentally add a blocking teardown step.
        var recording = new RecordingMinioBlobStore();
        var initializer = new MinioBucketInitializer(
            recording,
            NullLogger<MinioBucketInitializer>.Instance);

        await initializer.StopAsync(CancellationToken.None);

        Assert.Equal(0, recording.EnsureCallCount);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Constructor_throws_when_IBlobStore_is_not_MinioBlobStore()
    {
        // Defends the cast in the ctor: if a future Program.cs change
        // accidentally wires the initializer against the local CAS
        // backend, fail loud at startup rather than silently no-op.
        var localStore = new LocalCasBlobStore(
            Path.Combine(Path.GetTempPath(), "ontopilot-init-test-" + Guid.NewGuid().ToString("N")));

        var ex = Assert.Throws<ArgumentException>(() => new MinioBucketInitializer(
            localStore,
            NullLogger<MinioBucketInitializer>.Instance));
        Assert.Contains(nameof(MinioBlobStore), ex.Message);
    }

    // ------------------------------------------------------------------
    // Test fakes — subclass MinioBlobStore to intercept EnsureBucketExistsAsync.
    // ------------------------------------------------------------------

    private sealed class RecordingMinioBlobStore : MinioBlobStore
    {
        public int EnsureCallCount { get; private set; }

        public RecordingMinioBlobStore()
            : base(s3: new AmazonS3Client(
                      new Amazon.S3.AmazonS3Config { ServiceURL = "http://127.0.0.1:1", UseHttp = true }),
                  bucket: "recording")
        {
        }

        public override Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
        {
            EnsureCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingMinioBlobStore : MinioBlobStore
    {
        public FailingMinioBlobStore()
            : base(s3: new AmazonS3Client(
                      new Amazon.S3.AmazonS3Config { ServiceURL = "http://127.0.0.1:1", UseHttp = true }),
                  bucket: "failing")
        {
        }

        public override Task EnsureBucketExistsAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated bucket creation failure");
    }
}
