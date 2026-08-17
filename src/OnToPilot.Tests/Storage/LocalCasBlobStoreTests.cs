using System.Security.Cryptography;
using OnToPilot.Storage;

namespace OnToPilot.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="LocalCasBlobStore"/>. A shared temp root is created
/// per-fixture via <see cref="LocalCasFixture"/> and removed on dispose.
/// </summary>
/// <remarks>
/// All tests carry the <c>Storage</c> category so the stage gate (and any
/// downstream CI runs) can filter storage-only results.
/// </remarks>
public sealed class LocalCasBlobStoreTests : IClassFixture<LocalCasFixture>
{
    private readonly LocalCasFixture _fixture;

    public LocalCasBlobStoreTests(LocalCasFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The set of <see cref="IBlobStore"/> backends exercised by the storage
    /// contract tests. <see cref="MemberDataAttribute"/> requires a static data
    /// source, so the temp root is held statically on the fixture (created
    /// exactly once per fixture instance) and reused for every data row.
    /// </summary>
    public static IEnumerable<object[]> Stores
    {
        get
        {
            yield return new object[] { new LocalCasBlobStore(LocalCasFixture.TempRoot) };
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    [Trait("Category", "Storage")]
    public async Task Put_is_content_addressed_and_idempotent(IBlobStore store)
    {
        var bytes = "same content"u8.ToArray();
        var first = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);
        var second = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal($"{first.Sha256[..2]}/{first.Sha256[2..4]}/{first.Sha256}", first.LegacyStoragePath);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Put_returns_legacy_storage_path()
    {
        var store = _fixture.NewStore();
        var bytes = "hello world"u8.ToArray();

        var result = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal($"{result.Sha256[..2]}/{result.Sha256[2..4]}/{result.Sha256}", result.LegacyStoragePath);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Put_with_different_content_yields_different_sha()
    {
        var store = _fixture.NewStore();

        var first = await store.PutAsync(new MemoryStream("content-a"u8.ToArray()), CancellationToken.None);
        var second = await store.PutAsync(new MemoryStream("content-b"u8.ToArray()), CancellationToken.None);

        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Put_idempotent_does_not_overwrite()
    {
        var store = _fixture.NewStore();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var first = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);
        var pathOnDisk = Path.Combine(LocalCasFixture.TempRoot, first.LegacyStoragePath);
        var originalTimestamp = File.GetLastWriteTimeUtc(pathOnDisk);
        var originalContent = await File.ReadAllBytesAsync(pathOnDisk);

        // Wait long enough that a re-write would produce a clearly later timestamp.
        await Task.Delay(50);

        var second = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);
        var newTimestamp = File.GetLastWriteTimeUtc(pathOnDisk);
        var newContent = await File.ReadAllBytesAsync(pathOnDisk);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(originalContent, newContent);
        Assert.Equal(originalTimestamp, newTimestamp);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Get_returns_null_for_missing_sha()
    {
        var store = _fixture.NewStore();

        var stream = await store.GetAsync(new string('0', 64), CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Get_returns_stream_for_existing_sha()
    {
        var store = _fixture.NewStore();
        var bytes = "round-trip"u8.ToArray();
        var write = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        await using var stream = await store.GetAsync(write.Sha256, CancellationToken.None);

        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task ExistsAsync_true_for_present_false_for_absent()
    {
        var store = _fixture.NewStore();
        var bytes = "present"u8.ToArray();
        var write = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        var present = await store.ExistsAsync(write.Sha256, CancellationToken.None);
        var absent = await store.ExistsAsync(new string('f', 64), CancellationToken.None);

        Assert.True(present);
        Assert.False(absent);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task RemoveAsync_true_for_present_false_for_absent()
    {
        var store = _fixture.NewStore();
        var bytes = "removable"u8.ToArray();
        var write = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        var removedFirst = await store.RemoveAsync(write.Sha256, CancellationToken.None);
        var removedAgain = await store.RemoveAsync(write.Sha256, CancellationToken.None);

        Assert.True(removedFirst);
        Assert.False(removedAgain);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public async Task Put_large_stream_chunks_correctly()
    {
        var store = _fixture.NewStore();
        var bytes = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(bytes);

        // Compute the expected SHA-256 of the random payload once so the test
        // works deterministically — the random buffer is generated anew for
        // each invocation but its SHA-256 is recomputed next to the store's
        // own hash, both over the same byte array.
        var expectedSha = ComputeSha256Hex(bytes);

        var result = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(expectedSha, result.Sha256);
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        var digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

/// <summary>
/// Per-test-class temp directory shared by every <see cref="LocalCasBlobStoreTests"/>
/// case. Creates a unique subdirectory of <see cref="Path.GetTempPath"/> on
/// construction and wipes it on disposal.
/// </summary>
/// <remarks>
/// <para>
/// xUnit's <see cref="MemberDataAttribute"/> requires a static data source,
/// which is why the temp root is cached statically: xUnit guarantees a
/// single <see cref="IClassFixture{TFixture}"/> instance per test class
/// (constructor runs once, dispose once), so caching the root on first
/// access yields a real per-class directory without sacrificing parallel
/// safety across test classes.
/// </para>
/// </remarks>
public sealed class LocalCasFixture : IDisposable
{
    private static readonly object TempRootLock = new();
    private static string? _tempRoot;

    public LocalCasFixture()
    {
        lock (TempRootLock)
        {
            if (_tempRoot is null)
            {
                _tempRoot = Path.Combine(
                    Path.GetTempPath(),
                    $"ontopilot-blobtests-{Guid.NewGuid():N}");
                Directory.CreateDirectory(_tempRoot);
            }
        }
    }

    /// <summary>The directory backing every <see cref="LocalCasBlobStore"/> created by this fixture.</summary>
    public static string TempRoot =>
        _tempRoot ?? throw new InvalidOperationException("LocalCasFixture not yet initialized");

    /// <summary>Build a fresh <see cref="LocalCasBlobStore"/> rooted at <see cref="TempRoot"/>.</summary>
    public LocalCasBlobStore NewStore() => new(TempRoot);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (_tempRoot is not null && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
                _tempRoot = null;
            }
        }
        catch
        {
            // Best-effort cleanup; the OS will reap temp roots eventually.
        }
    }
}
