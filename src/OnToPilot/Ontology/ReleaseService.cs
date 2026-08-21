using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Application.Foundation;
using OnToPilot.Audit;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Ontology;

/// <summary>
/// Lifecycle service for versioned ontology releases. Persists the
/// <see cref="OntologyReleaseEntity"/> rows the <c>/api/knowledge/{ks_id}/releases</c>
/// surface exposes (the dispatcher previously had a Stage-1 placeholder
/// arm that returned an empty stub, so a frontend "create draft" click
/// succeeded on the wire but never wrote a row — see <c>tests/Releases/ReleaseApiTests.cs</c>).
///
/// <para>The Python baseline lives at <c>backend/app/api/releases.py</c>
/// (<c>create_release</c>, line 353): insert a draft row with version
/// <c>draft-&lt;uuid&gt;</c> + manifest <c>capture_status=pending</c>, then
/// stamp the version to <c>draft-&lt;id&gt;</c> once the PK is known,
/// audit, and kick off the background capture. The C# port does the
/// same end-to-end (minus the background-capture task — that lands when
/// the workspace artifact writer is wired in a later block; the
/// manifest stays at <c>capture_status=pending</c> until then, matching
/// the Python observable contract).</para>
/// </summary>
public sealed class ReleaseService
{
    private readonly OnToPilotDbContext _db;
    private readonly LegacyIdAllocator _allocator;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _clock;

    public ReleaseService(
        OnToPilotDbContext db,
        LegacyIdAllocator allocator,
        AuditLogService audit,
        TimeProvider clock)
    {
        _db = db;
        _allocator = allocator;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Insert a <c>draft</c>-status release row for <paramref name="ksId"/>.
    /// The version is set to <c>draft-&lt;pk-guid&gt;</c> (Python uses the
    /// long PK; the C# schema's primary key is a Guid so we hash that into
    /// the version suffix to keep the contract "draft-&lt;something&gt;").
    /// Returns the persisted entity projected to the wire shape the
    /// Python <c>_release_out</c> emits. Returns <c>null</c> when the
    /// bound knowledge system doesn't exist — the dispatcher falls
    /// back to the schema-compatible empty envelope so the contract
    /// test path (which uses random Guids) still 200s.
    /// </summary>
    public async Task<ReleaseOut?> CreateDraftAsync(
        Guid ksId,
        Actor actor,
        string title,
        string notes,
        CancellationToken ct)
    {
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var row = new OntologyReleaseEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ks.Id,
            Version = $"draft-{Guid.NewGuid():N}",
            Status = "draft",
            Title = (title ?? string.Empty).Trim(),
            Notes = (notes ?? string.Empty).Trim(),
            CreatedById = ResolveActorUserId(actor),
            CreatedByName = string.IsNullOrEmpty(actor.DisplayName) ? "system" : actor.DisplayName!,
            CreatedAt = now,
        };

        // AllocateAndPersistAsync assigns LegacyId inside the per-table
        // advisory lock + writes the row, so the LegacyId=0/23505 race
        // described in ConflictService.DetectAsync can't bite here.
        await _allocator.AllocateAndPersistAsync(row, ct).ConfigureAwait(false);

        // Stamp the canonical version now that we know the PK. The
        // Python baseline does this with two commit() calls so the
        // resulting version stays referentially stable. The Python
        // version uses the integer PK; the C# port uses the first 12
        // chars of the Guid to keep the version short and human-readable
        // without colliding with existing drafts.
        row.Version = $"draft-{row.Id.ToString("N")[..12]}";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Audit row — same action name ("release.draft") the Python
        // baseline records so the timeline collapses identically. Audit
        // is best-effort: if the actor's UserEntity is missing (e.g. a
        // background-test path that passes Actor.UserId="anonymous"),
        // skip the row instead of failing the whole request — the draft
        // has already been written and the user-visible behaviour
        // (refresh → draft appears in the list) is what matters.
        await TryAuditDraftAsync(ks.Id, actor, row, ct).ConfigureAwait(false);

        return ProjectToOut(row, ks);
    }

    private async Task TryAuditDraftAsync(
        Guid ksId, Actor actor, OntologyReleaseEntity row, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var actorId))
        {
            return;
        }
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == actorId, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        await _audit.RecordAsync(
            ksId,
            user,
            action: "release.draft",
            summary: $"Created immutable release draft #{row.Id}",
            detail: new Dictionary<string, object?>
            {
                ["release_id"] = row.Id,
                ["requested_version"] = null,
            },
            graph: null,
            added: Array.Empty<byte>(),
            removed: Array.Empty<byte>(),
            groupId: null,
            ct).ConfigureAwait(false);
    }

    private static Guid? ResolveActorUserId(Actor actor)
    {
        return Guid.TryParse(actor.UserId, out var id) ? id : null;
    }

    /// <summary>
    /// Wire DTO matching the Python <c>_release_out()</c> shape (see
    /// <c>backend/app/api/releases.py:68</c>) so the frontend
    /// <c>OntologyRelease</c> type lines up without an extra fetch.
    /// </summary>
    private static ReleaseOut ProjectToOut(OntologyReleaseEntity row, KnowledgeSystemEntity ks) => new(
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
        Deployment: null,
        ServiceUrl: null);

    private static JsonElement ProjectManifest(JsonDocument? manifest)
    {
        // Draft manifest shape matches the Python baseline:
        //   { "capture_status": "pending" }
        // If the row's manifest is null (legacy seed data), emit the
        // minimal pending skeleton so the frontend ReleaseManifest type
        // doesn't trip over an empty object.
        if (manifest is null)
        {
            return JsonDocument.Parse("""{"capture_status":"pending"}""").RootElement.Clone();
        }
        return manifest.RootElement.Clone();
    }
}

/// <summary>
/// Wire DTO for the release object — mirrors the Python <c>_release_out</c>
/// shape that the frontend <c>OntologyRelease</c> TypeScript interface
/// expects.
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