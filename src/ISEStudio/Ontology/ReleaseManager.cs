using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISEStudio.Ontology;

/// <summary>
/// Orchestrates the immutable release lifecycle backed by PostgreSQL release
/// statement snapshots and deployment rows.
/// </summary>
public sealed class ReleaseManager : IDisposable
{
    private readonly ISEStudioDbContext _db;
    private readonly IRdfStatementRepository _statements;
    private readonly ReleaseArtifactStore _artifacts;
    private readonly SemaphoreSlim _versionLock = new(1, 1);
    private bool _disposed;

    public ReleaseManager(
        ISEStudioDbContext db,
        IRdfStatementRepository statements,
        ReleaseArtifactStore artifacts)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    /// <summary>
    /// Freeze the three workspace layers into PostgreSQL release statements under the
    /// given <paramref name="releaseId"/> (the DB row's
    /// <c>Id.ToString("N")</c>) and <paramref name="version"/> (the draft
    /// version string). All three layers are snapshotted inside their own
    /// layer rows so publish, rollback, and delete can address the same
    /// immutable snapshot.
    /// </summary>
    public async Task<Release> CaptureAsync(
        KsContext ks,
        string releaseId,
        string version,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _versionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CapturePostgresAsync(ks, releaseId, version, actor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _versionLock.Release();
        }
    }

    /// <summary>
    /// Re-save the draft row with the public version assigned at publish time.
    /// </summary>
    public void FinalizeVersion(string releaseId, string publishedVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentException.ThrowIfNullOrEmpty(publishedVersion);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Guid.TryParse(releaseId, out var releaseGuid)) throw new InvalidOperationException("Invalid release id.");
        var release = _db.OntologyReleases.Find(releaseGuid)
            ?? throw new InvalidOperationException($"Release '{releaseId}' does not exist.");
        release.Version = publishedVersion;
        _db.SaveChanges();
    }

    // ------------------------------------------------------------------
    // Publish
    // ------------------------------------------------------------------

    /// <summary>
    /// Publish a previously captured release by activating its PostgreSQL
    /// deployment row.
    /// </summary>
    public async Task<Release> PublishAsync(
        string releaseId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await PublishPostgresAsync(releaseId, actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Record the owning KsContext into the artifact directory so publish
    /// (or a restart) can re-open the serving store without going back to
    /// the EF layer.
    /// </summary>
    internal static void WriteKsHeader(string artifactPath, KsContext ks)
    {
        var headerPath = Path.Combine(artifactPath, "ks.json");
        File.WriteAllText(headerPath,
            System.Text.Json.JsonSerializer.Serialize(new { ks.GraphIri, ks.BaseIri }));
    }

    private static KsContext? ReadKsHeader(string artifactPath)
    {
        var headerPath = Path.Combine(artifactPath, "ks.json");
        if (!File.Exists(headerPath)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(headerPath));
            var root = doc.RootElement;
            var graphIri = root.GetProperty("GraphIri").GetString() ?? "";
            var baseIri = root.GetProperty("BaseIri").GetString() ?? "";
            return new KsContext(graphIri, baseIri);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Read published
    // ------------------------------------------------------------------

    /// <summary>
    /// Read quads from the published serving store. Throws if the release
    /// has not been published.
    /// </summary>
    public IReadOnlyList<RdfStatement> ReadPublished(string releaseId, RdfLayer layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Guid.TryParse(releaseId, out var releaseGuid))
            throw new InvalidOperationException($"Release '{releaseId}' is invalid.");
        var release = _db.OntologyReleases.FirstOrDefault(item => item.Id == releaseGuid)
            ?? throw new InvalidOperationException($"Release '{releaseId}' does not exist.");
        if (release.Status == "deleted")
            throw new InvalidOperationException($"Release '{releaseId}' has been deleted.");
        var ks = _db.KnowledgeSystems.Where(item => item.Id == release.KnowledgeSystemId)
            .Select(item => new KsContext(item.GraphIri, item.BaseIri)).First();
        var rows = _db.ReleaseStatements.AsNoTracking()
            .Where(item => item.ReleaseId == releaseGuid && item.Layer == layer.ToString())
            .ToList();
        return rows.Select(ToStatement).ToList();
    }

    /// <summary>True once a release has been published and its serving store is open.</summary>
    public bool IsPublished(string releaseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        return Guid.TryParse(releaseId, out var releaseGuid)
            && _db.ReleaseDeployments.Any(item => item.ReleaseId == releaseGuid && item.Status == "active");
    }

    private async Task<Release> CapturePostgresAsync(
        KsContext ks, string releaseId, string version, Actor actor, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(releaseId, out var releaseGuid))
            throw new InvalidOperationException($"Release id '{releaseId}' must be a GUID.");
        var existingRelease = await _db!.OntologyReleases.FirstOrDefaultAsync(item => item.Id == releaseGuid, cancellationToken)
            .ConfigureAwait(false);
        if (existingRelease is null)
        {
            var now = DateTimeOffset.UtcNow;
            _db.OntologyReleases.Add(new OntologyReleaseEntity
            {
                Id = releaseGuid,
                KnowledgeSystemId = ks.KnowledgeSystemId,
                Version = version,
                Status = "draft",
                Title = string.Empty,
                Notes = string.Empty,
                SnapshotDir = string.Empty,
                CreatedByName = actor.UserId,
                CreatedAt = now,
            });
        }
        else
        {
            existingRelease.Status = "draft";
            existingRelease.Version = version;
        }
        var existing = await _db!.ReleaseStatements.Where(item => item.ReleaseId == releaseGuid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.ReleaseStatements.RemoveRange(existing);
        var files = new List<ReleaseFileManifest>(3);
        long provenanceCount = 0;
        foreach (var layer in new[] { RdfLayer.TBox, RdfLayer.ABox, RdfLayer.Vocabulary })
        {
            var layerName = layer.ToString();
            var graphIri = GraphIriFor(ks, layer);
            var statements = await _statements!.ListAsync(ks.KnowledgeSystemId, layerName, cancellationToken)
                .ConfigureAwait(false);
            var layerStatements = statements.Where(statement => statement.GraphIri == graphIri).ToList();
            _db.ReleaseStatements.AddRange(layerStatements.Select(statement => new ReleaseStatementEntity
            {
                KnowledgeSystemId = ks.KnowledgeSystemId,
                ReleaseId = releaseGuid,
                Layer = layerName,
                GraphIri = statement.GraphIri,
                SubjectIri = statement.Subject is RdfIri iri ? iri.Value : statement.Subject.ToString() ?? string.Empty,
                PredicateIri = statement.PredicateIri,
                ObjectIri = statement.Object is RdfIri objectIri ? objectIri.Value : null,
                ObjectValue = statement.Object is RdfLiteral literal ? literal.Value : null,
                StatementHash = HashStatement(statement),
            }));
            var nQuads = RdfExportService.SerializeNQuads(layerStatements);
            _artifacts.Write(releaseId, layer, nQuads);
            files.Add(_artifacts.BuildFileManifest(releaseId, layer, nQuads));
            provenanceCount += ReleaseArtifactStore.StatementCount(nQuads);
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var manifest = new ReleaseManifest(version, files, provenanceCount);
        _artifacts.SaveManifest(releaseId, manifest);
        return new Release(releaseId, version, ks, _artifacts.ReleasePath(releaseId));
    }

    private static string HashStatement(RdfStatement statement) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{statement.Subject}|{statement.PredicateIri}|{statement.Object}|{statement.GraphIri}")));

    private static RdfStatement ToStatement(ReleaseStatementEntity row)
    {
        var subject = new RdfIri(row.SubjectIri);
        var predicateIri = row.PredicateIri;
        var obj = row.ObjectIri is not null
            ? (RdfTerm)new RdfIri(row.ObjectIri)
            : new RdfLiteral(row.ObjectValue ?? string.Empty);
        return new RdfStatement(subject, predicateIri, obj, row.GraphIri);
    }

    // ------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------

    /// <summary>
    /// Delete a release: close its serving store, remove the artifact
    /// subdirectory, and free the version slot for reuse.
    /// </summary>
    public Task DeleteAsync(
        string releaseId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Guid.TryParse(releaseId, out var releaseGuid)) return Task.CompletedTask;
        _db.ReleaseStatements.RemoveRange(_db.ReleaseStatements.Where(item => item.ReleaseId == releaseGuid));
        _db.ReleaseDeployments.RemoveRange(_db.ReleaseDeployments.Where(item => item.ReleaseId == releaseGuid));
        _db.ReleaseStatementProvenances.RemoveRange(_db.ReleaseStatementProvenances.Where(item => item.ReleaseId == releaseGuid));
        _db.ExportJobs.RemoveRange(_db.ExportJobs.Where(item => item.ReleaseId == releaseGuid));
        _db.OntologyReleases.RemoveRange(_db.OntologyReleases.Where(item => item.Id == releaseGuid));
        return _db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // Version allocation
    // ------------------------------------------------------------------

    /// <summary>
    /// Allocate the next free version string ("v1", "v2", …). Reuses
    /// numerically lowest freed slot so a delete-then-capture reuses v1.
    /// </summary>
    public string AllocateVersion()
    {
        var postgresUsed = _db.OntologyReleases
            .Where(item => item.Status != "deleted")
            .Select(item => item.Version).ToHashSet(StringComparer.Ordinal);
        for (int i = 1; i < int.MaxValue; i++)
        {
            if (!postgresUsed.Contains($"v{i}")) return $"v{i}";
        }
        throw new InvalidOperationException("No free version slots.");
    }

    /// <summary>List versions of captured releases (artifact-dir-derived).</summary>
    public IReadOnlyList<string> ListVersions() => _db.OntologyReleases.Select(item => item.Version).ToList();

    private async Task<Release> PublishPostgresAsync(
        string releaseId, Actor actor, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(releaseId, out var releaseGuid))
            throw new InvalidOperationException($"Release '{releaseId}' is invalid.");
        var release = await _db!.OntologyReleases.FirstOrDefaultAsync(item => item.Id == releaseGuid, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release '{releaseId}' does not exist.");
        var ks = await _db.KnowledgeSystems.FirstAsync(item => item.Id == release.KnowledgeSystemId, cancellationToken)
            .ConfigureAwait(false);
        var rows = await _db.ReleaseStatements.Where(item => item.ReleaseId == releaseGuid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var deployment = await _db.ReleaseDeployments.FirstOrDefaultAsync(item => item.ReleaseId == releaseGuid, cancellationToken)
            .ConfigureAwait(false);
        if (deployment is null)
        {
            deployment = new ReleaseDeploymentEntity
            {
                KnowledgeSystemId = release.KnowledgeSystemId,
                ReleaseId = releaseGuid,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.ReleaseDeployments.Add(deployment);
        }
        deployment.Status = "active";
        deployment.TboxGraphIri = ks.GraphIri.TrimEnd('/');
        deployment.AboxGraphIri = deployment.TboxGraphIri + "/abox";
        deployment.VocabularyGraphIri = deployment.TboxGraphIri + "/vocabulary";
        deployment.StatementCount = rows.Count;
        deployment.ActivatedAt = DateTimeOffset.UtcNow;
        release.Status = "published";
        release.PublishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new Release(releaseId, release.Version, KsContext.FromEntity(ks), string.Empty);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    internal static string GraphIriFor(KsContext ks, RdfLayer layer) => layer switch
    {
        RdfLayer.TBox => ks.TBoxGraph,
        RdfLayer.ABox => ks.ABoxGraph,
        RdfLayer.Vocabulary => ks.VocabularyGraph,
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _versionLock.Dispose();
    }
}