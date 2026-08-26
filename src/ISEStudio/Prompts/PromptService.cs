using Microsoft.EntityFrameworkCore;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Audit;
using ISEStudio.Authorization;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Prompts;

/// <summary>
/// Per-knowledge-system prompt overrides. The static
/// <see cref="PromptCatalog"/> defines what prompts exist; this service
/// stores per-KS override rows on
/// <see cref="KnowledgePromptOverrideEntity"/> and merges them into the
/// wire shape on read. All write paths record an audit event so the
/// history surface can render override provenance.
/// </summary>
public sealed class PromptService
{
    private readonly ISEStudioDbContext _db;
    private readonly KnowledgeSystemAccessService _access;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _clock;

    public PromptService(
        ISEStudioDbContext db,
        KnowledgeSystemAccessService access,
        AuditLogService audit,
        TimeProvider clock)
    {
        _db = db;
        _access = access;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Merge the static <see cref="PromptCatalog"/> with this KS's override
    /// rows into the wire-shape list. Returns <c>null</c> when the KS is
    /// missing or invisible to the actor (mapped to 404 by the dispatcher);
    /// throws <see cref="ValidationException"/> when the actor lacks
    /// <see cref="KSRole.Viewer"/> (mapped to 403 by the dispatcher).
    /// </summary>
    public async Task<PromptListOut?> ListAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Viewer)
            throw new ValidationException("Viewer access required to list prompts.");

        var overrides = await _db.KnowledgePromptOverrides.AsNoTracking()
            .Where(o => o.KnowledgeSystemId == ksId)
            .ToDictionaryAsync(o => o.PromptKey, ct)
            .ConfigureAwait(false);

        var items = PromptCatalog.All.Select(def =>
        {
            overrides.TryGetValue(def.Key, out var ov);
            return new PromptOut(
                def.Key,
                def.Category,
                def.Title,
                def.Description,
                def.DefaultContent,
                ov?.Content ?? def.DefaultContent,
                def.Variables,
                ov is not null,
                ov?.UpdatedAt,
                ov?.UpdatedByName);
        }).ToList();

        return new PromptListOut(items, overrides.Count);
    }

    /// <summary>
    /// Upsert an override row for <paramref name="promptKey"/>. Throws
    /// <see cref="KeyNotFoundException"/> when the key isn't in
    /// <see cref="PromptCatalog"/>, <see cref="ValidationException"/>
    /// for empty content or insufficient role.
    /// </summary>
    public async Task<PromptOut?> UpdateAsync(
        Guid ksId,
        string promptKey,
        string content,
        Actor actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ValidationException("content must not be empty");
        var def = PromptCatalog.Find(promptKey)
            ?? throw new KeyNotFoundException($"Unknown prompt '{promptKey}'.");

        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to update prompt.");

        var now = _clock.GetUtcNow();
        var existing = await _db.KnowledgePromptOverrides
            .FirstOrDefaultAsync(o => o.KnowledgeSystemId == ksId && o.PromptKey == promptKey, ct)
            .ConfigureAwait(false);

        KnowledgePromptOverrideEntity row;
        if (existing is null)
        {
            row = new KnowledgePromptOverrideEntity
            {
                Id = Guid.NewGuid(),
                KnowledgeSystemId = ksId,
                PromptKey = promptKey,
                Content = content,
                UpdatedById = user.Id,
                UpdatedByName = user.DisplayName ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
            };
            // LegacyId is filled by the column DEFAULT 0 at INSERT time.
            _db.KnowledgePromptOverrides.Add(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            existing.Content = content;
            existing.UpdatedById = user.Id;
            existing.UpdatedByName = user.DisplayName ?? string.Empty;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            row = existing;
        }

        await _audit.RecordAsync(
            ksId,
            user,
            "system.prompt.override",
            $"Updated '{promptKey}' prompt override.",
            null,
            null,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            null,
            ct).ConfigureAwait(false);

        return new PromptOut(
            def.Key,
            def.Category,
            def.Title,
            def.Description,
            def.DefaultContent,
            row.Content,
            def.Variables,
            true,
            (DateTimeOffset?)row.UpdatedAt,
            row.UpdatedByName);
    }

    /// <summary>
    /// Remove an override row (no-op when missing) and audit it. The returned
    /// <see cref="PromptOut"/> reflects the default state
    /// (<c>is_overridden=false</c>) whether or not a row existed.
    /// </summary>
    public async Task<PromptOut?> RestoreAsync(
        Guid ksId,
        string promptKey,
        Actor actor,
        CancellationToken ct)
    {
        var def = PromptCatalog.Find(promptKey)
            ?? throw new KeyNotFoundException($"Unknown prompt '{promptKey}'.");

        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to restore prompt.");

        var existing = await _db.KnowledgePromptOverrides
            .FirstOrDefaultAsync(o => o.KnowledgeSystemId == ksId && o.PromptKey == promptKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _db.KnowledgePromptOverrides.Remove(existing);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await _audit.RecordAsync(
                ksId,
                user,
                "system.prompt.restore",
                $"Restored '{promptKey}' prompt to default.",
                null,
                null,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                null,
                ct).ConfigureAwait(false);
        }

        return new PromptOut(
            def.Key,
            def.Category,
            def.Title,
            def.Description,
            def.DefaultContent,
            def.DefaultContent,
            def.Variables,
            false,
            null,
            null);
    }

    /// <summary>
    /// Remove every override row for this KS in one transaction and write a
    /// single aggregate audit row whose <c>detail</c> enumerates the keys
    /// that were restored. Returns the number of rows removed.
    /// </summary>
    public async Task<int> RestoreAllAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return 0;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
            throw new ValidationException("Editor access required to restore all prompts.");

        var rows = await _db.KnowledgePromptOverrides
            .Where(o => o.KnowledgeSystemId == ksId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return 0;

        var keys = rows.Select(r => r.PromptKey).ToArray();
        _db.KnowledgePromptOverrides.RemoveRange(rows);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detail = new Dictionary<string, object?> { ["restored_keys"] = keys };
        await _audit.RecordAsync(
            ksId,
            user,
            "system.prompt.restore_all",
            $"Restored {rows.Count} prompt overrides.",
            detail,
            null,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            null,
            ct).ConfigureAwait(false);

        return rows.Count;
    }

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> ResolveUserAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return (null, null);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        if (ks is null) return (null, null);
        return (user, ks);
    }
}