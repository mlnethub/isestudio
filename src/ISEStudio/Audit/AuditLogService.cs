using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Audit;

/// <summary>
/// Shared, audited, allocation-safe writer for
/// <see cref="AuditEventEntity"/> rows. Centralises the
/// JSON-serialise-then-allocate-then-persist pattern so callers (RDF
/// import, ontology edits, vocabulary writes) don't reinvent it per
/// slice. The byte-exact N-quads diffs are stored verbatim in
/// <see cref="AuditEventEntity.Added"/> / <see cref="AuditEventEntity.Removed"/>
/// so a rollback can replay the inverse against the live store.
/// </summary>
public sealed class AuditLogService
{
    private readonly LegacyIdAllocator _allocator;
    private readonly TimeProvider _clock;

    public AuditLogService(LegacyIdAllocator allocator, TimeProvider clock)
    {
        _allocator = allocator;
        _clock = clock;
    }

    /// <summary>
    /// Persist a single audit row scoped to <paramref name="ksId"/>. The
    /// <c>Added</c> / <c>Removed</c> blobs round-trip as-is — empty
    /// byte arrays are stored as <c>null</c> so the SQL row matches the
    /// existing <c>ontology.edit</c> / <c>ontology.reset</c> contract
    /// (no-changes rows never materialise an empty byte[]). The
    /// allocator assigns <see cref="LegacyAddressableEntity.LegacyId"/>
    /// inside its per-table advisory lock so two concurrent writers
    /// can't both insert <c>LegacyId = 0</c>.
    /// </summary>
    public async Task RecordAsync(
        Guid ksId,
        UserEntity actor,
        string action,
        string summary,
        IReadOnlyDictionary<string, object?>? detail,
        string? graph,
        byte[] added,
        byte[] removed,
        string? groupId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        JsonDocument? detailDoc = null;
        if (detail is not null)
        {
            detailDoc = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        }

        await _allocator.AllocateAndPersistAsync(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = string.IsNullOrEmpty(actor.DisplayName) ? actor.Username : actor.DisplayName!,
            Action = action,
            Summary = summary,
            Detail = detailDoc,
            Graph = graph,
            GroupId = groupId,
            Added = added.Length == 0 ? null : added,
            Removed = removed.Length == 0 ? null : removed,
            CreatedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);
    }
}