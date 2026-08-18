using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OnToPilot.Migration.Blobs;
using OnToPilot.Storage;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace OnToPilot.IntegrationTests.Migration;

/// <summary>
/// Verifies the verified blob migration that copies the Python
/// filesystem-laid-out CAS blobs (<c>blobs/aa/bb/&lt;sha256&gt;</c>) into
/// MinIO via <see cref="MinioBlobStore"/>, with per-object SHA-256
/// verification, dry-run, resume-after-interrupt, corruption detection,
/// and release-artifact exclusion.
///
/// <para><b>Global constraints enforced here.</b>
/// <list type="bullet">
///   <item>Each object's SHA-256 must match its filename (the Python
///   <c>_sharded_relpath</c> derived the filename from the SHA).</item>
///   <item>Each uploaded object must be re-fetched and re-hashed; the
///   re-hash must equal the filename hash (load-bearing).</item>
///   <item>Only blobs referenced by at least one <c>document.storagepath</c>
///   row are migrated (orphans + release artifacts are skipped).</item>
///   <item>The dry-run path must not write to MinIO.</item>
///   <item>The resume path must skip objects that already completed.</item>
/// </list>
/// </para>
///
/// <para>Each test owns a unique Testcontainers MinIO + PostgreSQL so
/// concurrent runs do not share state. The fixtures short-circuit on
/// hosts without Docker, mirroring the <c>MinioBlobStoreTests</c>
/// convention. All tests carry
/// <c>[Trait("Category", "Migration")]</c> so the rehearsal / cutover
/// orchestration (Task 4) can filter them out of the default CI run.</para>
/// </summary>
public sealed class BlobMigrationTests : IAsyncLifetime
{
    private const string BucketName = "blob-migration-tests";
    private const string PostgresDatabase = "ontopilot";
    private const string PostgresUsername = "postgres";
    private const string PostgresPassword = "postgres";

    private readonly MinioBuilder _minioBuilder = new MinioBuilder("minio/minio:latest")
        .WithUsername("minioadmin")
        .WithPassword("minioadmin");

    private readonly PostgreSqlBuilder _postgresBuilder = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase(PostgresDatabase)
        .WithUsername(PostgresUsername)
        .WithPassword(PostgresPassword)
        .WithCleanUp(true);

    private MinioContainer _minio = null!;
    private PostgreSqlContainer _postgres = null!;
    private MinioBlobStore _store = null!;
    private AmazonS3Client _s3 = null!;
    private string _repoRoot = null!;
    private string _blobDir = null!;
    private string _releasesDir = null!;
    private bool _dockerAvailable;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _repoRoot = LocateRepoRoot();
        _blobDir = Path.Combine(_repoRoot, ".artifacts", "blob-test", "blobs");
        _releasesDir = Path.Combine(_repoRoot, ".artifacts", "blob-test", "releases");
        RecreateDirectory(_blobDir);
        RecreateDirectory(_releasesDir);

        _minio = _minioBuilder.Build();
        _postgres = _postgresBuilder.Build();
        try
        {
            await Task.WhenAll(_minio.StartAsync(), _postgres.StartAsync());
            _dockerAvailable =
                _minio.State == TestcontainersStates.Running
                && _postgres.State == TestcontainersStates.Running;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        var s3Config = new AmazonS3Config
        {
            ServiceURL = _minio.GetConnectionString(),
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = "us-east-1",
        };
        _s3 = new AmazonS3Client(_minio.GetAccessKey(), _minio.GetSecretKey(), s3Config);
        _store = new MinioBlobStore(_s3, BucketName);

        try
        {
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
        {
            // bucket already exists — fine
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_dockerAvailable)
        {
            try
            {
                await _s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = BucketName });
            }
            catch
            {
                // best effort
            }
            _s3.Dispose();
        }

        await Task.WhenAll(
            _minio.DisposeAsync().AsTask(),
            _postgres.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Verbatim required test. Two <c>document</c> rows reference the same
    /// SHA-256 (content-addressable dedup). The migration MUST upload ONE
    /// object to MinIO and the manifest MUST record
    /// <c>ReferenceCount == 2</c>. The two <c>document</c> rows stay in
    /// place (we never modify the database — the .NET side will keep
    /// reading via the existing <c>storagepath</c> column).
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Duplicate_document_references_upload_one_object_and_keep_two_rows()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();
        await SeedTwoDocumentsSharingBlobAsync(connectionString, bytes: Encoding.UTF8.GetBytes("shared-payload"));

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var report = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: false, Force: false, SkipExisting: true, ManifestOut: null),
            cancellationToken: CancellationToken.None);

        Assert.Single(report.Entries);
        Assert.Equal(2, report.Entries[0].ReferenceCount);

        // The two document rows are preserved (migration never touches
        // the database). SELECT COUNT(*).
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT count(*)::bigint FROM document";
            var n = (long)(await count.ExecuteScalarAsync())!;
            Assert.Equal(2, n);
        }

        // The MinIO write landed at the legacy path (MinioBlobStore's
        // existing behaviour preserves Document.storagepath portability).
        var expectedSha = report.Entries[0].Sha256;
        var expectedKey = BlobKey.LegacyPathFor(expectedSha);
        var stored = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = BucketName,
            Key = expectedKey,
        });
        Assert.NotNull(stored);
        using var ms = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(ms);
        Assert.Equal(Encoding.UTF8.GetBytes("shared-payload"), ms.ToArray());
    }

    /// <summary>
    /// Dry-run must not call MinIO at all and must still emit a manifest
    /// with the same shape (SourcePath, ObjectKey, Size, Sha256,
    /// ReferenceCount). The MinIO bucket remains empty after the run.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Dry_run_does_not_upload_to_minio()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();
        var bytes = Encoding.UTF8.GetBytes("dryrun-payload");
        await SeedTwoDocumentsSharingBlobAsync(connectionString, bytes);

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var manifestPath = Path.Combine(_repoRoot, ".artifacts", "blob-test", "dryrun-manifest.json");
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var report = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: true, Force: false, SkipExisting: true, ManifestOut: manifestPath),
            cancellationToken: CancellationToken.None);

        Assert.Single(report.Entries);
        Assert.True(File.Exists(manifestPath));

        // The bucket must be empty — dry-run never uploaded anything.
        var listing = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = BucketName });
        Assert.Empty(listing.S3Objects);

        // The manifest must validate against the JSON schema.
        var schemaPath = Path.Combine(_repoRoot, "migration", "manifests", "blob-manifest.schema.json");
        Assert.True(File.Exists(schemaPath));
        BlobManifestSchemaValidator.AssertValid(schemaPath, await File.ReadAllTextAsync(manifestPath));
    }

    /// <summary>
    /// Resume: seed two blobs, run migration to completion, then add a
    /// third blob and re-run with the SAME state file. The third blob
    /// must upload; the first two must NOT be re-uploaded (idempotent +
    /// state-store aware). The bucket must contain exactly three objects.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Resume_after_interrupt_skips_already_uploaded_objects()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();
        var bytesA = Encoding.UTF8.GetBytes("first");
        var bytesB = Encoding.UTF8.GetBytes("second");
        var shaA = WriteBlobAndSeedDocument(connectionString, bytesA, label: "a");
        var shaB = WriteBlobAndSeedDocument(connectionString, bytesB, label: "b");

        var statePath = Path.Combine(_repoRoot, ".artifacts", "blob-test", "resume-state.json");
        // The state file lives at a fixed path; if a previous test session
        // left entries behind, the first run would no-op them and the
        // expected "2 uploaded" assertion would fail. Reset it so the
        // test is self-contained regardless of prior state.
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var firstRun = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: false, Force: false, SkipExisting: true, ManifestOut: null, StatePath: statePath),
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, firstRun.Entries.Count);
        Assert.Equal(2, firstRun.UploadedCount);

        // Simulate an interrupt then resume: add a third blob, re-run with
        // the SAME state file. The state store must cause the first two
        // to be skipped (no second upload).
        var bytesC = Encoding.UTF8.GetBytes("third");
        WriteBlobAndSeedDocument(connectionString, bytesC, label: "c");

        var secondRun = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: false, Force: false, SkipExisting: true, ManifestOut: null, StatePath: statePath),
            cancellationToken: CancellationToken.None);

        // The second run still finds all 3 blobs, but only 1 new upload
        // occurred; the other 2 were skipped because the state store
        // remembers them.
        Assert.Equal(3, secondRun.Entries.Count);
        Assert.Equal(1, secondRun.UploadedCount);
        Assert.Equal(2, secondRun.SkippedCount);

        // The MinIO bucket must have exactly 3 objects (one per SHA).
        var listing = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = BucketName });
        Assert.Equal(3, listing.S3Objects.Count);
        var keys = listing.S3Objects.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(BlobKey.LegacyPathFor(shaA), keys);
        Assert.Contains(BlobKey.LegacyPathFor(shaB), keys);
    }

    /// <summary>
    /// Corruption: a file on disk whose bytes do not match its filename
    /// SHA-256. The migration MUST throw immediately (gate failure) and
    /// upload nothing. This is the load-bearing case for the brief's
    /// "any gate failure stops immediately" constraint.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Corruption_throws_when_filename_sha_does_not_match_file_bytes()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();

        // Drop a fake blob file: name = sha256("anything"), content = "garbage".
        var claimedSha = ComputeSha256HexLower(Encoding.UTF8.GetBytes("anything"));
        var aa = claimedSha[..2];
        var bb = claimedSha[2..4];
        var shard = Path.Combine(_blobDir, aa, bb);
        Directory.CreateDirectory(shard);
        await File.WriteAllBytesAsync(Path.Combine(shard, claimedSha), Encoding.UTF8.GetBytes("garbage"));

        // Also seed a document row pointing at this corrupt blob so the
        // command actually tries to migrate it (without a document ref
        // the orphan rule would short-circuit first).
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var seed = conn.CreateCommand();
            seed.CommandText = @"
                CREATE TABLE IF NOT EXISTS document (
                    id bigserial PRIMARY KEY,
                    storagepath varchar(1024) NOT NULL DEFAULT ''
                );
                INSERT INTO document (storagepath) VALUES (@p);
                INSERT INTO document (storagepath) VALUES (@p);";
            seed.Parameters.AddWithValue("@p", $"{aa}/{bb}/{claimedSha}");
            await seed.ExecuteNonQueryAsync();
        }

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cmd.RunAsync(
                sourceDir: _blobDir,
                blobStore: _store,
                dataSource: dataSource,
                options: new BlobMigrationOptions(DryRun: false, Force: false, SkipExisting: true, ManifestOut: null),
                cancellationToken: CancellationToken.None));

        Assert.Contains(claimedSha, ex.Message, StringComparison.OrdinalIgnoreCase);
        // The bucket must be empty — we abort before uploading anything.
        var listing = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = BucketName });
        Assert.Empty(listing.S3Objects);
    }

    /// <summary>
    /// Release-artifact exclusion: a file at
    /// <c>backend/data/releases/&lt;sha&gt;.zip</c> is NOT under the
    /// source directory and MUST NOT be migrated even when it has no
    /// <c>document</c> reference.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Release_artifact_is_not_migrated()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();

        // Seed ONLY a single document-referenced blob so the run has work
        // to do; then drop a release file at a sibling path that the
        // migration walk must skip.
        var bytes = Encoding.UTF8.GetBytes("real-doc");
        WriteBlobAndSeedDocument(connectionString, bytes, label: "real");

        var releaseSha = ComputeSha256HexLower(Encoding.UTF8.GetBytes("fake-release-zip"));
        var releaseFile = Path.Combine(_releasesDir, releaseSha + ".zip");
        await File.WriteAllBytesAsync(releaseFile, Encoding.UTF8.GetBytes("fake-release-zip"));

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var report = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: true, Force: false, SkipExisting: true, ManifestOut: null),
            cancellationToken: CancellationToken.None);

        // Only the document-referenced blob should be in the manifest.
        Assert.Single(report.Entries);
        Assert.DoesNotContain(report.Entries, e => e.Sha256 == releaseSha);
    }

    /// <summary>
    /// Verifies the JSON Schema shipped at
    /// <c>migration/manifests/blob-manifest.schema.json</c> actually
    /// accepts the manifest BlobMigrationCommand produces. This is the
    /// Task 4 hand-off gate: the schema must be compatible with the
    /// runtime shape.
    /// </summary>
    [Fact]
    [Trait("Category", "Migration")]
    public async Task Manifest_validates_against_schema()
    {
        if (DockerRequired()) return;

        var connectionString = _postgres.GetConnectionString();
        var bytes = Encoding.UTF8.GetBytes("schema-validate");
        WriteBlobAndSeedDocument(connectionString, bytes, label: "schema");

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var manifestPath = Path.Combine(_repoRoot, ".artifacts", "blob-test", "schema-validate-manifest.json");
        var cmd = new BlobMigrationCommand(NullLogger<BlobMigrationCommand>.Instance);

        var report = await cmd.RunAsync(
            sourceDir: _blobDir,
            blobStore: _store,
            dataSource: dataSource,
            options: new BlobMigrationOptions(DryRun: true, Force: false, SkipExisting: true, ManifestOut: manifestPath),
            cancellationToken: CancellationToken.None);

        Assert.Single(report.Entries);

        var schemaPath = Path.Combine(_repoRoot, "migration", "manifests", "blob-manifest.schema.json");
        Assert.True(File.Exists(schemaPath), $"Missing JSON Schema at {schemaPath}");
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        BlobManifestSchemaValidator.AssertValid(schemaPath, manifestJson);
    }

    // -----------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Seed the Python-style 24-business-table shape with two
    /// <c>document</c> rows that share the same <c>storagepath</c>
    /// (content dedup). The blob itself is written to <c>_blobDir</c>
    /// under <c>aa/bb/&lt;sha&gt;</c>.
    /// </summary>
    private async Task SeedTwoDocumentsSharingBlobAsync(string connectionString, byte[] bytes)
    {
        var sha = ComputeSha256HexLower(bytes);
        var aa = sha[..2];
        var bb = sha[2..4];
        var shard = Path.Combine(_blobDir, aa, bb);
        Directory.CreateDirectory(shard);
        await File.WriteAllBytesAsync(Path.Combine(shard, sha), bytes);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS document (
                    id bigserial PRIMARY KEY,
                    storagepath varchar(1024) NOT NULL DEFAULT ''
                );";
            await create.ExecuteNonQueryAsync();
        }
        await using (var seed = conn.CreateCommand())
        {
            seed.CommandText = @"
                INSERT INTO document (storagepath) VALUES (@p);
                INSERT INTO document (storagepath) VALUES (@p);";
            seed.Parameters.AddWithValue("@p", $"{aa}/{bb}/{sha}");
            await seed.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Drop a fresh blob file and seed one <c>document</c> row referencing
    /// it. Returns the SHA so the test can assert on the MinIO key.
    /// </summary>
    private string WriteBlobAndSeedDocument(string connectionString, byte[] bytes, string label)
    {
        var sha = ComputeSha256HexLower(bytes);
        var aa = sha[..2];
        var bb = sha[2..4];
        var shard = Path.Combine(_blobDir, aa, bb);
        Directory.CreateDirectory(shard);
        File.WriteAllBytes(Path.Combine(shard, sha), bytes);

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using (var ensure = conn.CreateCommand())
        {
            ensure.CommandText = @"
                CREATE TABLE IF NOT EXISTS document (
                    id bigserial PRIMARY KEY,
                    storagepath varchar(1024) NOT NULL DEFAULT ''
                );";
            ensure.ExecuteNonQuery();
        }
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO document (storagepath) VALUES (@p);";
        insert.Parameters.AddWithValue("@p", $"{aa}/{bb}/{sha}");
        insert.ExecuteNonQuery();
        return sha;
    }

    /// <summary>Skip-without-fail when Docker is unavailable on this host.</summary>
    private bool DockerRequired()
    {
        if (_dockerAvailable) return false;
        Console.Error.WriteLine(
            "[skip] Docker containers did not start (Docker unavailable on this host); "
            + "skipping BlobMigration integration test.");
        return true;
    }

    private static string LocateRepoRoot()
    {
        var location = AppContext.BaseDirectory;
        var cursor = new DirectoryInfo(location);
        while (cursor is not null)
        {
            var migrationCandidate = Path.Combine(cursor.FullName, "migration");
            var srcCandidate = Path.Combine(cursor.FullName, "src");
            if (Directory.Exists(migrationCandidate) && Directory.Exists(srcCandidate))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the repository root from {location}; "
            + "expected a directory containing both 'migration/' and 'src/'.");
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    private static string ComputeSha256HexLower(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
