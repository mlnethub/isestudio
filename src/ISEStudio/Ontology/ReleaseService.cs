using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ISEStudio.Application.Foundation;
using ISEStudio.Audit;
using ISEStudio.Conflicts;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Knowledge;

namespace ISEStudio.Ontology;

/// <summary>
/// Lifecycle service for versioned ontology releases. Persists the
/// <see cref="OntologyReleaseEntity"/> rows the
/// <c>/api/knowledge/{ks_id}/releases</c> surface exposes, drives the
/// immutable snapshot capture (background), and ties the
/// <see cref="ReleaseManager"/> artifact/serving-store engine to the DB
/// row via the release id (<c>Id.ToString("N")</c>) + version.
///
/// <para>Mirrors <c>backend/app/api/releases.py</c>: create drafts a row
/// + kicks off a background capture; review runs the quality gate;
/// publish assigns the public <c>v{N}</c> and materialises the
/// per-release read-only serving store via <see cref="ReleaseManager"/>;
/// deploy/stop/delete manage the deployment row; rollback restores the
/// workspace graphs from the snapshot; diff computes a per-layer
/// semantic set-diff.</para>
/// </summary>
public sealed class ReleaseService
{
    private readonly ISEStudioDbContext _db;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _clock;
    private readonly ReleaseManager _releases;
    private readonly ABoxValidator _aboxValidator;
    private readonly KnowledgeStatsService _stats;
    private readonly ConflictService _conflicts;
    private readonly StoreWrapper? _store;

    public ReleaseService(
        ISEStudioDbContext db,
        AuditLogService audit,
        TimeProvider clock,
        ReleaseManager releases,
        ABoxValidator aboxValidator,
        KnowledgeStatsService stats,
        ConflictService conflicts,
        StoreWrapper? store)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _releases = releases;
        _aboxValidator = aboxValidator;
        _stats = stats;
        _conflicts = conflicts;
        _store = store;
    }

    // ----------------------------------------------------------------------
    // Create (draft + background capture)
    // ----------------------------------------------------------------------

    public async Task<ReleaseOut?> CreateDraftAsync(
        Guid ksId, Actor actor, string title, string notes, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var now = _clock.GetUtcNow();
        var row = new OntologyReleaseEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            Version = $"draft-{Guid.NewGuid():N}",
            Status = "draft",
            Title = (title ?? string.Empty).Trim(),
            Notes = (notes ?? string.Empty).Trim(),
            Manifest = JsonDocument.Parse("""{"capture_status":"pending"}"""),
            CreatedById = ResolveActorUserId(actor),
            CreatedByName = string.IsNullOrEmpty(actor.DisplayName) ? "system" : actor.DisplayName!,
            CreatedAt = now,
        };
        _db.OntologyReleases.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        row.Version = $"draft-{row.Id.ToString("N")[..12]}";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.draft",
            $"Created immutable release draft #{row.Id}",
            new Dictionary<string, object?> { ["release_id"] = row.Id }, ct).ConfigureAwait(false);

        // Capture the immutable snapshot synchronously (MVP — Python kicks
        // this off in the background; the .NET background Task.Run +
        // IDbContextFactory path is deferred to a hardening pass). Blocks
        // the response until the three layers are sharded; small graphs
        // finish in milliseconds. Failure marks capture_status=failed so
        // review surfaces "snapshot is not ready".
        var releaseKey = row.Id.ToString("N");
        try
        {
            await _releases.CaptureAsync(KsContext.FromEntity(ks), releaseKey, row.Version, actor, ct)
                .ConfigureAwait(false);
            row.SnapshotDir = _releases.Artifacts.ReleasePath(releaseKey);
            row.Manifest = JsonDocument.Parse(
                $$"""{"capture_status":"ready","version":"{{row.Version}}"}""");
        }
        catch (Exception ex)
        {
            row.Manifest = JsonDocument.Parse(
                $$"""{"capture_status":"failed","error":{{JsonSerializer.Serialize(ex.Message)}}}""");
        }
        // Persist the manifest update with CancellationToken.None so the
        // capture result survives even when the HTTP client cancelled
        // mid-capture (timeout / disconnect). If the request ct were
        // used here, the OperationCanceledException from CaptureAsync
        // would be caught and manifest set to "failed", but then
        // SaveChangesAsync(ct) would also throw (ct is cancelled) —
        // leaving the row stuck at capture_status="pending" forever.
        await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return ProjectToOut(row, ks, deployment: null, publicId: ks.PublicId);
    }

    // ----------------------------------------------------------------------
    // List
    // ----------------------------------------------------------------------

    public async Task<object?> ListAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var rows = await _db.OntologyReleases.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ks.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var deployments = await _db.ReleaseDeployments.AsNoTracking()
            .Where(d => d.KnowledgeSystemId == ks.Id)
            .ToDictionaryAsync(d => d.ReleaseId, ct).ConfigureAwait(false);

        var items = rows.Select(r => ProjectToOut(r, ks,
            deployments.GetValueOrDefault(r.Id), ks.PublicId)).ToList();
        return new { items, total = items.Count };
    }

    // ----------------------------------------------------------------------
    // Review (quality gate)
    // ----------------------------------------------------------------------

    public async Task<ReleaseOut?> ReviewAsync(
        Guid ksId, Guid releaseId, Actor actor, string? note, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        if (row.Status != "draft")
            throw new ResourceInUseException("Only draft releases can be reviewed.");
        if (!CaptureReady(row))
            throw new ResourceInUseException("Release snapshot is not ready.");

        var gate = await QualityGateAsync(ks.Id, ct).ConfigureAwait(false);
        var blocking = (int)gate.GetType().GetProperty("blocking")!.GetValue(gate)!;
        if (blocking > 0)
        {
            // Re-stamp the gate onto the manifest so the UI can show it.
            row.Manifest = JsonDocument.Parse(
                JsonSerializer.Serialize(new { capture_status = "ready", quality_gate = gate }));
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw new ResourceInUseException(
                $"Release quality gate failed: {blocking} blocking issue(s).");
        }

        row.Status = "reviewed";
        row.ReviewedById = ResolveActorUserId(actor);
        row.ReviewedByName = string.IsNullOrEmpty(actor.DisplayName) ? "system" : actor.DisplayName!;
        row.ReviewedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.review",
            $"Approved release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["version"] = row.Version },
            ct).ConfigureAwait(false);
        return ProjectToOut(row, ks, await DeploymentForAsync(row.Id, ct), ks.PublicId);
    }

    // ----------------------------------------------------------------------
    // Publish
    // ----------------------------------------------------------------------

    public async Task<ReleaseOut?> PublishAsync(
        Guid ksId, Guid releaseId, Actor actor, string? note, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        if (row.Status != "reviewed")
            throw new ResourceInUseException("Only reviewed releases can be published.");
        if (!CaptureReady(row))
            throw new ResourceInUseException("Release snapshot is not ready.");

        var releaseKey = row.Id.ToString("N");
        var version = await NextVersionAsync(ks.Id, ct).ConfigureAwait(false);
        row.Version = version;
        _releases.FinalizeVersion(releaseKey, version);

        // Materialise the per-release read-only serving store (synchronous
        // MVP; Python provisions in the background). Idempotent if already
        // published.
        await _releases.PublishAsync(releaseKey, actor, ct).ConfigureAwait(false);

        row.Status = "published";
        row.PublishedById = ResolveActorUserId(actor);
        row.PublishedByName = string.IsNullOrEmpty(actor.DisplayName) ? "system" : actor.DisplayName!;
        row.PublishedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var deployment = await EnsureDeploymentAsync(ks, row, ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.publish",
            $"Published release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["version"] = row.Version, ["deployment_id"] = deployment.Id },
            ct).ConfigureAwait(false);
        return ProjectToOut(row, ks, deployment, ks.PublicId);
    }

    // ----------------------------------------------------------------------
    // Deploy / Stop
    // ----------------------------------------------------------------------

    public async Task<ReleaseOut?> DeployAsync(
        Guid ksId, Guid releaseId, Actor actor, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        if (row.Status == "deleted")
            throw new KeyNotFoundException("Release has been deleted.");
        if (row.Status != "published")
            throw new ResourceInUseException("Only published releases can be served.");
        if (!CaptureReady(row))
            throw new ResourceInUseException("Release snapshot is not ready.");

        var existing = await DeploymentForAsync(row.Id, ct).ConfigureAwait(false);
        if (existing is not null && existing.Status is "active" or "provisioning")
            return ProjectToOut(row, ks, existing, ks.PublicId);

        var deployment = await EnsureDeploymentAsync(ks, row, ct).ConfigureAwait(false);
        // Re-materialise the serving store in case it was closed by a stop.
        await _releases.PublishAsync(row.Id.ToString("N"), actor, ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.deploy",
            $"Started service deployment for release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["deployment_id"] = deployment.Id },
            ct).ConfigureAwait(false);
        return ProjectToOut(row, ks, deployment, ks.PublicId);
    }

    public async Task<ReleaseOut?> StopDeploymentAsync(
        Guid ksId, Guid releaseId, Actor actor, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        var deployment = await DeploymentForAsync(row.Id, ct).ConfigureAwait(false);
        if (deployment is null || deployment.Status == "stopped")
            return ProjectToOut(row, ks, deployment, ks.PublicId);

        deployment.Status = "stopped";
        deployment.StoppedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Close the serving store.
        await _releases.DeleteAsync(row.Id.ToString("N"), actor, ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.undeploy",
            $"Stopped service for release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["deployment_id"] = deployment.Id },
            ct).ConfigureAwait(false);
        return ProjectToOut(row, ks, deployment, ks.PublicId);
    }

    // ----------------------------------------------------------------------
    // Delete
    // ----------------------------------------------------------------------

    public async Task<ReleaseOut?> DeleteAsync(
        Guid ksId, Guid releaseId, Actor actor, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        var deployment = await DeploymentForAsync(row.Id, ct).ConfigureAwait(false);
        if (row.Status == "deleted")
            return ProjectToOut(row, ks, deployment, ks.PublicId);

        // With synchronous capture (MVP), a "pending" capture_status means
        // the create-draft request was interrupted (client cancel / crash)
        // — there is no actual background capture running. The dispatcher's
        // RunWithExtractionGuardAsync already guards against real concurrent
        // extractions, so this stale "pending" check only blocks the user
        // from deleting stuck drafts. Removed to let the user clean up.

        var previousStatus = row.Status;
        row.Status = "deleted";
        // Flip capture_status to "deleted" so the UI stops showing
        // "正在生成" after the release is deleted.
        row.Manifest = WithCaptureStatus(row.Manifest, "deleted");
        if (deployment is not null && deployment.Status != "stopped")
        {
            deployment.Status = "stopping";
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Close serving store + drop artifacts.
        await _releases.DeleteAsync(row.Id.ToString("N"), actor, ct).ConfigureAwait(false);
        await TryAuditAsync(ks.Id, actor, "release.delete",
            $"Deleted release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["version"] = row.Version, ["previous_status"] = previousStatus },
            ct).ConfigureAwait(false);
        return ProjectToOut(row, ks, deployment, ks.PublicId);
    }

    // ----------------------------------------------------------------------
    // Rollback
    // ----------------------------------------------------------------------

    public async Task<object?> RollbackAsync(
        Guid ksId, Guid releaseId, Actor actor, CancellationToken ct)
    {
        var (ks, row) = await ResolveReleaseAsync(ksId, releaseId, ct).ConfigureAwait(false);
        if (ks is null || row is null) return null;
        if (row.Status == "deleted")
            throw new KeyNotFoundException("Release has been deleted.");
        if (!CaptureReady(row))
            throw new ResourceInUseException("Release snapshot is not ready.");

        if (_store is null)
            throw new InvalidOperationException("Graph store is not available.");

        var releaseKey = row.Id.ToString("N");
        var ksc = KsContext.FromEntity(ks);
        // Restore each workspace layer from the immutable snapshot.
        foreach (var layer in new[] { RdfLayer.TBox, RdfLayer.ABox, RdfLayer.Vocabulary })
        {
            var graphIri = ReleaseManager.GraphIriFor(ksc, layer);
            var snapshot = _releases.Artifacts.Read(releaseKey, layer);
            _store.ReplaceGraphFromNQuads(graphIri, snapshot);
        }

        // Clear governance queues (mirrors Python: delete all conflicts +
        // pending resolutions + pending terms, then re-sync).
        await _db.Conflicts.Where(c => c.KnowledgeSystemId == ks.Id)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await _db.EntityResolutions.Where(r => r.KnowledgeSystemId == ks.Id && r.Status == "pending")
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await _db.TermProposals.Where(t => t.KnowledgeSystemId == ks.Id && t.Status == "pending")
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _stats.RefreshAsync(ks.Id, ct).ConfigureAwait(false);
        await _conflicts.SyncAfterOntologyMutationAsync(ks.Id, semantic: false, ct)
            .ConfigureAwait(false);

        await TryAuditAsync(ks.Id, actor, "release.rollback",
            $"Restored release {row.Version}",
            new Dictionary<string, object?> { ["release_id"] = row.Id, ["version"] = row.Version },
            ct).ConfigureAwait(false);
        return new { restored = row.Id, version = row.Version };
    }

    // ----------------------------------------------------------------------
    // Diff
    // ----------------------------------------------------------------------

    public async Task<object?> DiffAsync(
        Guid ksId, Guid fromId, Guid toId, Actor actor, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return null;

        var left = await _db.OntologyReleases.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == fromId && r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        var right = await _db.OntologyReleases.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == toId && r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        if (left is null || right is null)
            throw new KeyNotFoundException("Release not found.");
        if (left.Status == "deleted" || right.Status == "deleted")
            throw new KeyNotFoundException("A selected release has been deleted.");
        if (!CaptureReady(left) || !CaptureReady(right))
            throw new ResourceInUseException("Both release snapshots must be ready.");

        var leftKey = left.Id.ToString("N");
        var rightKey = right.Id.ToString("N");
        var layers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var layer in new[] { RdfLayer.TBox, RdfLayer.ABox, RdfLayer.Vocabulary })
        {
            var leftLines = Lines(_releases.Artifacts.Read(leftKey, layer));
            var rightLines = Lines(_releases.Artifacts.Read(rightKey, layer));
            var added = rightLines.Except(leftLines).ToList();
            var removed = leftLines.Except(rightLines).ToList();
            layers[layerName(layer)] = new
            {
                added = added.Count,
                removed = removed.Count,
                added_sample = added.Take(20).ToList(),
                removed_sample = removed.Take(20).ToList(),
            };
        }
        return new
        {
            @from = new { id = left.Id, version = left.Version },
            to = new { id = right.Id, version = right.Version },
            layers,
        };
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private async Task<(KnowledgeSystemEntity? ks, OntologyReleaseEntity? row)> ResolveReleaseAsync(
        Guid ksId, Guid releaseId, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        if (ks is null) return (null, null);
        var row = await _db.OntologyReleases
            .FirstOrDefaultAsync(r => r.Id == releaseId && r.KnowledgeSystemId == ks.Id, ct)
            .ConfigureAwait(false);
        return (ks, row);
    }

    private async Task<ReleaseDeploymentEntity?> DeploymentForAsync(Guid releaseId, CancellationToken ct) =>
        await _db.ReleaseDeployments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ReleaseId == releaseId, ct).ConfigureAwait(false);

    private async Task<ReleaseDeploymentEntity> EnsureDeploymentAsync(
        KnowledgeSystemEntity ks, OntologyReleaseEntity row, CancellationToken ct)
    {
        var ksc = KsContext.FromEntity(ks);
        var existing = await _db.ReleaseDeployments
            .FirstOrDefaultAsync(d => d.ReleaseId == row.Id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Status = "active";
            existing.TboxGraphIri = ksc.TBoxGraph;
            existing.VocabularyGraphIri = ksc.VocabularyGraph;
            existing.AboxGraphIri = ksc.ABoxGraph;
            existing.Error = null;
            existing.ActivatedAt = _clock.GetUtcNow();
            existing.StoppedAt = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }
        var deployment = new ReleaseDeploymentEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            ReleaseId = row.Id,
            Status = "active",
            TboxGraphIri = ksc.TBoxGraph,
            VocabularyGraphIri = ksc.VocabularyGraph,
            AboxGraphIri = ksc.ABoxGraph,
            CreatedAt = _clock.GetUtcNow(),
            ActivatedAt = _clock.GetUtcNow(),
        };
        _db.ReleaseDeployments.Add(deployment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return deployment;
    }

    private async Task<string> NextVersionAsync(Guid ksId, CancellationToken ct)
    {
        var published = await _db.OntologyReleases.AsNoTracking()
            .Where(r => r.KnowledgeSystemId == ksId && r.Status == "published")
            .Select(r => r.Version).ToListAsync(ct).ConfigureAwait(false);
        var max = published
            .Select(v => int.TryParse(v.StartsWith("v") ? v[1..] : null, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"v{max + 1}";
    }

    private async Task<object> QualityGateAsync(Guid ksId, CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstAsync(k => k.Id == ksId, ct).ConfigureAwait(false);
        var ksc = KsContext.FromEntity(ks);
        int openErrors = await _db.Conflicts.CountAsync(
            c => c.KnowledgeSystemId == ksId && c.Status == "open" && c.Severity == "error", ct)
            .ConfigureAwait(false);
        int unresolved = await _db.EntityResolutions.CountAsync(
            r => r.KnowledgeSystemId == ksId && r.Status == "pending", ct).ConfigureAwait(false);
        int pendingTerms = await _db.TermProposals.CountAsync(
            t => t.KnowledgeSystemId == ksId && t.Status == "pending", ct).ConfigureAwait(false);
        int validationErrors = _aboxValidator.Validate(ksc).ErrorCount;
        int blocking = openErrors + unresolved + pendingTerms + validationErrors;
        return new
        {
            open_conflict_errors = openErrors,
            unresolved_entities = unresolved,
            pending_terminology = pendingTerms,
            validation_errors = validationErrors,
            blocking,
        };
    }

    private static bool CaptureReady(OntologyReleaseEntity row)
    {
        if (row.Manifest is null) return false;
        return row.Manifest.RootElement.TryGetProperty("capture_status", out var s)
            && s.ValueKind == JsonValueKind.String
            && s.GetString() == "ready";
    }

    /// <summary>
    /// Return a copy of <paramref name="manifest"/> with its
    /// <c>capture_status</c> field replaced by <paramref name="status"/>.
    /// Preserves all other fields (version, sha256, error, …). Returns a
    /// fresh <see cref="JsonDocument"/> because the source is immutable.
    /// </summary>
    private static JsonDocument WithCaptureStatus(JsonDocument? manifest, string status)
    {
        JsonObject? node = null;
        if (manifest is not null)
        {
            node = JsonNode.Parse(manifest.RootElement.GetRawText()) as JsonObject;
        }
        node ??= new JsonObject();
        node["capture_status"] = status;
        return JsonDocument.Parse(node.ToJsonString());
    }

    private static List<string> Lines(byte[] nQuads)
    {
        using var ms = new MemoryStream(nQuads);
        using var reader = new StreamReader(ms);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        return lines;
    }

    private static string layerName(RdfLayer layer) => layer switch
    {
        RdfLayer.TBox => "tbox",
        RdfLayer.ABox => "abox",
        RdfLayer.Vocabulary => "vocabulary",
        _ => layer.ToString().ToLowerInvariant(),
    };

    private async Task TryAuditAsync(
        Guid ksId, Actor actor, string action, string summary,
        Dictionary<string, object?> detail, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var actorId)) return;
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == actorId, ct).ConfigureAwait(false);
        if (user is null) return;
        await _audit.RecordAsync(ksId, user, action, summary, detail,
            graph: null, added: Array.Empty<byte>(), removed: Array.Empty<byte>(),
            groupId: null, ct).ConfigureAwait(false);
    }

    private static Guid? ResolveActorUserId(Actor actor) =>
        Guid.TryParse(actor.UserId, out var id) ? id : null;

    private static ReleaseOut ProjectToOut(
        OntologyReleaseEntity row, KnowledgeSystemEntity ks,
        ReleaseDeploymentEntity? deployment, string publicId)
    {
        var serviceUrl = deployment is not null
            ? $"/api/v1/knowledge-systems/{publicId}/releases/{row.Version}" : null;
        return new ReleaseOut(
            Id: row.Id,
            KnowledgeSystemId: ks.Id,
            Version: row.Version,
            Status: row.Status,
            Title: row.Title,
            Notes: row.Notes,
            Manifest: ProjectManifest(row.Manifest),
            CreatedBy: row.CreatedByName,
            ReviewedBy: string.IsNullOrEmpty(row.ReviewedByName) ? null : row.ReviewedByName,
            PublishedBy: string.IsNullOrEmpty(row.PublishedByName) ? null : row.PublishedByName,
            CreatedAt: row.CreatedAt,
            ReviewedAt: row.ReviewedAt,
            PublishedAt: row.PublishedAt,
            Deployment: ProjectDeployment(deployment),
            ServiceUrl: serviceUrl);
    }

    private static object? ProjectDeployment(ReleaseDeploymentEntity? d) =>
        d is null ? null : new
        {
            id = d.Id,
            status = d.Status,
            statement_count = d.StatementCount,
            provenance_count = d.ProvenanceCount,
            error = d.Error,
            activated_at = d.ActivatedAt,
            stopped_at = d.StoppedAt,
        };

    private static JsonElement ProjectManifest(JsonDocument? manifest)
    {
        if (manifest is null)
            return JsonDocument.Parse("""{"capture_status":"pending"}""").RootElement.Clone();
        return manifest.RootElement.Clone();
    }
}

/// <summary>
/// Wire DTO matching the Python <c>_release_out()</c> shape
/// (<c>backend/app/api/releases.py:68</c>) so the frontend
/// <c>OntologyRelease</c> TypeScript interface lines up.
/// </summary>
public sealed record ReleaseOut(
    Guid Id,
    Guid KnowledgeSystemId,
    string Version,
    string Status,
    string Title,
    string Notes,
    JsonElement Manifest,
    string CreatedBy,
    string? ReviewedBy,
    string? PublishedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? PublishedAt,
    object? Deployment,
    string? ServiceUrl);
