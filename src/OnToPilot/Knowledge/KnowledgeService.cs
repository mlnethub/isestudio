using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Application.Foundation;
using OnToPilot.Authorization;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Knowledge;

/// <summary>
/// Knowledge-system CRUD + membership + review-stats surface. Aligned
/// with the Python <c>backend/app/api/knowledge.py</c> module (417 LOC).
///
/// <para>The service is the dispatcher-routed KS admin seam; controllers
/// bind <c>GET / POST / PATCH / DELETE /api/knowledge*</c> through
/// <see cref="OnToPilot.Integration.InternalOperationDispatcher"/> and the
/// dispatcher hands a scoped instance of this service the
/// <see cref="OnToPilotDbContext"/> already opened for the request.</para>
///
/// <para>Role gates mirror the Python dependencies: <c>list</c> /
/// <c>get</c> require <see cref="KSRole.Viewer"/>, <c>create</c> / <c>update</c>
/// require <see cref="KSRole.Editor"/> on the KS (or admin), <c>delete</c>
/// + membership ops require <see cref="KSRole.Owner"/> on the KS (or
/// admin). Non-admins only see KS rows they own or have an explicit grant
/// on &mdash; same as <c>accessible_ks_ids</c> in Python.</para>
/// </summary>
public sealed class KnowledgeService
{
    /// <summary>Maximum length on the <c>name</c> field (matches the SQLModel column).</summary>
    public const int MaxNameLength = 200;

    /// <summary>Maximum length on the <c>description</c> field.</summary>
    public const int MaxDescriptionLength = 2000;

    private readonly OnToPilotDbContext _db;
    private readonly TimeProvider _clock;
    private readonly KnowledgeSystemAccessService _access;
    private readonly LegacyIdAllocator _allocator;
    private readonly KnowledgeStatsService _stats;

    public KnowledgeService(
        OnToPilotDbContext db,
        TimeProvider clock,
        KnowledgeSystemAccessService access,
        LegacyIdAllocator allocator,
        KnowledgeStatsService stats)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _allocator = allocator;
        _stats = stats;
    }

    // ----------------------------------------------------------------------
    // List / get / create / update / delete
    // ----------------------------------------------------------------------

    /// <summary>
    /// List every KS the requesting user can see. Admins see all; others
    /// see only KS they own or have an explicit grant on.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeSystemOut>> ListAsync(Actor actor, CancellationToken ct)
    {
        var user = await ResolveUserAsync(actor, ct).ConfigureAwait(false);
        if (user is null) return Array.Empty<KnowledgeSystemOut>();

        var query = _db.KnowledgeSystems.AsNoTracking();

        if (!user.IsAdmin)
        {
            // Accessible = owned OR granted. We push both arms into a
            // HashSet<Guid> filter so SQLite can resolve it with a single
            // IN clause.
            var ownedIds = await _db.KnowledgeSystems.AsNoTracking()
                .Where(k => k.OwnerId == user.Id)
                .Select(k => k.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var grantedIds = await _db.KSGrants.AsNoTracking()
                .Where(g => g.UserId == user.Id)
                .Select(g => g.KnowledgeSystemId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var accessibleIds = new HashSet<Guid>(ownedIds);
            foreach (var id in grantedIds) accessibleIds.Add(id);
            if (accessibleIds.Count == 0) return Array.Empty<KnowledgeSystemOut>();
            query = query.Where(k => accessibleIds.Contains(k.Id));
        }

        // SQLite's EF Core provider refuses DateTimeOffset in ORDER BY;
        // materialise the rows first and sort on the client. Production
        // runs against PostgreSQL where this branch isn't hit — the extra
        // round-trip is test-only overhead.
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return await ProjectAsync(rows, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch a single KS. <see cref="KSRole.None"/> callers get null so
    /// the dispatcher can map to 404.
    /// </summary>
    public async Task<KnowledgeSystemOut?> GetAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role == KSRole.None) return null;
        var projection = await ProjectAsync(new[] { ks }, user, ct).ConfigureAwait(false);
        return projection[0];
    }

    /// <summary>
    /// Recompute the cached <c>ClassCount / PropertyCount / AxiomCount</c>
    /// columns from the live TBox graph and return the refreshed DTO.
    /// Mirrors Python's <c>POST /api/knowledge/{id}/refresh_stats</c>
    /// operator-repair path. Editor+ only &mdash; the call mutates
    /// derived stats and bumps <c>UpdatedAt</c>, so it has the same
    /// privilege bar as a content edit.
    /// </summary>
    public async Task<KnowledgeSystemOut?> RefreshStatsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
        {
            throw new ValidationException(
                "Editor access is required to refresh knowledge-system stats.");
        }

        await _stats.RefreshAsync(ksId, ct).ConfigureAwait(false);

        // ProjectAsync reads from the change-tracked entity, so the
        // refreshed counts flow straight into the DTO without an extra
        // round-trip. Reload via AsNoTracking so the post-SaveChanges
        // values surface even if the EF tracker is stale.
        await _db.Entry(ks).ReloadAsync(ct).ConfigureAwait(false);
        var projection = await ProjectAsync(new[] { ks }, user, ct).ConfigureAwait(false);
        return projection[0];
    }

    /// <summary>
    /// Create a new KS, owned by the calling user. The graph_iri /
    /// base_iri are derived from the assigned <c>LegacyId</c> so the
    /// first row gets id=1, the next gets id=2, etc. — the wire contract
    /// from Python.
    /// </summary>
    public async Task<KnowledgeSystemOut> CreateAsync(CreateKnowledgeSystemRequest req, Actor actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw new ValidationException("name is required.");
        }
        if (req.Name.Trim().Length > MaxNameLength)
        {
            throw new ValidationException($"name must be {MaxNameLength} characters or fewer.");
        }
        var user = await ResolveUserAsync(actor, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Authenticated user not found.");

        var ks = new KnowledgeSystemEntity
        {
            PublicId = Guid.NewGuid().ToString("N"),
            Name = req.Name.Trim(),
            Description = req.Description ?? string.Empty,
            OwnerId = user.Id,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
            LlmModel = NullIfBlank(req.LlmModel),
            LlmProviderId = req.LlmProviderId,
            EmbeddingProviderId = req.EmbeddingProviderId,
            EmbeddingModel = NullIfBlank(req.EmbeddingModel),
        };
        // Atomic alloc+save: holds the knowledge_systems advisory lock
        // until COMMIT so concurrent CreateAsync calls can't observe the
        // same MAX+1 and race on the UNIQUE(legacy_id) constraint. The
        // GraphIri/BaseIri embed the assigned LegacyId, so they are
        // patched in by a second SaveChanges below.
        await _allocator.AllocateAndPersistAsync(ks, ct).ConfigureAwait(false);
        ks.GraphIri = $"http://ontopilot.local/ks/{ks.LegacyId}";
        ks.BaseIri = $"http://ontopilot.local/ks/{ks.LegacyId}/onto#";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var projection = await ProjectAsync(new[] { ks }, user, ct).ConfigureAwait(false);
        return projection[0];
    }

    /// <summary>
    /// Three-valued PATCH. Each field has three states:
    /// <c>null</c> = absent (don't touch), <c>""</c> = clear, non-empty =
    /// set. Matches Python <c>UpdateKS</c> exactly.
    /// </summary>
    public async Task<KnowledgeSystemOut?> UpdateAsync(
        Guid ksId, UpdateKnowledgeSystemRequest req, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Editor)
        {
            throw new ValidationException(
                "Editor access is required to update a knowledge system.");
        }

        if (req.Name is not null)
        {
            var trimmed = req.Name.Trim();
            if (trimmed.Length == 0)
            {
                throw new ValidationException("name cannot be empty.");
            }
            if (trimmed.Length > MaxNameLength)
            {
                throw new ValidationException($"name must be {MaxNameLength} characters or fewer.");
            }
            ks.Name = trimmed;
        }
        if (req.Description is not null)
        {
            if (req.Description.Length > MaxDescriptionLength)
            {
                throw new ValidationException(
                    $"description must be {MaxDescriptionLength} characters or fewer.");
            }
            ks.Description = req.Description;
        }
        if (req.LlmModel is not null)
        {
            ks.LlmModel = NullIfBlank(req.LlmModel);
        }
        if (req.LlmProviderId is not null)
        {
            // Guid.Empty = clear override
            ks.LlmProviderId = req.LlmProviderId == Guid.Empty ? null : req.LlmProviderId;
        }
        if (req.EmbeddingProviderId is not null)
        {
            ks.EmbeddingProviderId = req.EmbeddingProviderId == Guid.Empty ? null : req.EmbeddingProviderId;
        }
        if (req.EmbeddingModel is not null)
        {
            ks.EmbeddingModel = NullIfBlank(req.EmbeddingModel);
        }
        ks.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await WriteAuditAsync(ks.Id, user, "ks.update", "Updated knowledge system settings", null, ct)
            .ConfigureAwait(false);

        var projection = await ProjectAsync(new[] { ks }, user, ct).ConfigureAwait(false);
        return projection[0];
    }

    /// <summary>
    /// Hard-delete a KS and every per-KS row that references it. Mirrors
    /// Python <c>delete_ks</c>'s SQL-side cascade. The RDF graph + blob
    /// cleanup steps in Python (clear_graph, blobstore.delete, shutil.rmtree)
    /// are <em>not</em> performed here yet — those need a wired
    /// <c>StoreWrapper</c> + <c>BlobStore</c> (Block 5 / 6). When those
    /// land, append the cleanup at the end of this method.
    /// </summary>
    public async Task<Guid?> DeleteAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Owner)
        {
            throw new ValidationException(
                "Owner access is required to delete a knowledge system.");
        }

        var ksIdGuid = ks.Id;

        // Per-KS SQL cleanup. The list mirrors the Python loop at
        // knowledge.py:243-248 — every per-KS scoped row must be cleared
        // because KS ids can be reused.
        await DeletePerKsRowsAsync<AxiomProvenanceEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<AboxProvenanceEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ReleaseStatementProvenanceEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ReleaseDeploymentEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ExportJobEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<OntologyReleaseEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ExtractionJobEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<KSGrantEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<KnowledgeApiTokenEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<McpUserTokenEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<KnowledgePromptOverrideEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<EntityResolutionEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<TermProposalEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ConflictEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<AuditEventEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<TboxReconciliationEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<ValidationDecisionEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);
        await DeletePerKsRowsAsync<DocumentEntity>(p => p.KnowledgeSystemId == ksIdGuid, ct);

        _db.KnowledgeSystems.Remove(ks);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return ks.Id;
    }

    // ----------------------------------------------------------------------
    // Membership
    // ----------------------------------------------------------------------

    /// <summary>List owner + explicit grants for a KS. Viewer-readable.</summary>
    public async Task<IReadOnlyList<MemberOut>?> ListMembersAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role == KSRole.None) return null;

        var result = new List<MemberOut>();
        if (ks.OwnerId.HasValue)
        {
            var owner = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == ks.OwnerId.Value, ct)
                .ConfigureAwait(false);
            if (owner is not null)
            {
                result.Add(new MemberOut(
                    UserId: owner.Id,
                    Username: owner.Username,
                    DisplayName: owner.DisplayName,
                    Role: "owner"));
            }
        }
        var grants = await _db.KSGrants.AsNoTracking()
            .Where(g => g.KnowledgeSystemId == ks.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var grantUserIds = grants.Select(g => g.UserId).Distinct().ToList();
        var grantUsers = await _db.Users.AsNoTracking()
            .Where(u => grantUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct)
            .ConfigureAwait(false);
        foreach (var grant in grants)
        {
            if (!grantUsers.TryGetValue(grant.UserId, out var u)) continue;
            result.Add(new MemberOut(
                UserId: u.Id,
                Username: u.Username,
                DisplayName: u.DisplayName,
                Role: grant.Role));
        }
        return result;
    }

    /// <summary>
    /// Add or update a grant. Rejects if the target is the owner (Python
    /// parity) or if the role is not <c>viewer</c> / <c>editor</c>.
    /// </summary>
    public async Task<IReadOnlyList<MemberOut>?> AddMemberAsync(
        Guid ksId, AddMemberRequest req, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Owner)
        {
            throw new ValidationException(
                "Owner access is required to manage members.");
        }

        var roleNorm = (req.Role ?? "viewer").Trim().ToLowerInvariant();
        if (roleNorm is not ("viewer" or "editor"))
        {
            throw new ValidationException("role must be viewer or editor.");
        }

        var username = (req.Username ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            throw new ValidationException("username is required.");
        }
        var target = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, ct)
            .ConfigureAwait(false);
        if (target is null)
        {
            throw new InvalidOperationException("User not found.");
        }
        if (ks.OwnerId.HasValue && ks.OwnerId.Value == target.Id)
        {
            throw new ValidationException("This user is the owner.");
        }

        var existing = await _db.KSGrants
            .FirstOrDefaultAsync(g => g.KnowledgeSystemId == ks.Id && g.UserId == target.Id, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            var grant = new KSGrantEntity
            {
                KnowledgeSystemId = ks.Id,
                UserId = target.Id,
                Role = roleNorm,
                CreatedAt = _clock.GetUtcNow(),
            };
            // AllocateAndPersistAsync holds the per-table
            // pg_advisory_xact_lock until COMMIT so two concurrent
            // add-member calls cannot both INSERT legacy_id=0 and
            // collide on ux_ksgrant_legacy_id.
            await _allocator.AllocateAndPersistAsync(grant, ct).ConfigureAwait(false);
        }
        else
        {
            existing.Role = roleNorm;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await WriteAuditAsync(ks.Id, user, "member.add",
            $"Granted {roleNorm} to \"{target.Username}\"",
            BuildDetail(target.Id, roleNorm), ct).ConfigureAwait(false);

        return await ListMembersAsync(ksId, actor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drop a grant. The grant is silently missing &rarr; 0 removed (the
    /// caller is the owner either way; Python returns <c>{removed: id}</c>
    /// unconditionally so a missing grant returns id without an error).
    /// </summary>
    public async Task<Guid?> RemoveMemberAsync(
        Guid ksId, Guid userId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Owner)
        {
            throw new ValidationException(
                "Owner access is required to manage members.");
        }

        var target = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        if (target is null) return userId;

        var grant = await _db.KSGrants
            .FirstOrDefaultAsync(g => g.KnowledgeSystemId == ks.Id && g.UserId == target.Id, ct)
            .ConfigureAwait(false);
        if (grant is not null)
        {
            _db.KSGrants.Remove(grant);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await WriteAuditAsync(ks.Id, user, "member.remove",
                $"Removed member \"{target.Username}\"",
                BuildDetail(userId, null), ct).ConfigureAwait(false);
        }
        return userId;
    }

    /// <summary>
    /// Active users the owner can still grant access to (not already a
    /// member / the owner). Mirrors Python <c>grantable_users</c>.
    /// </summary>
    public async Task<IReadOnlyList<GrantableUserOut>?> GrantableUsersAsync(
        Guid ksId, string? query, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role < KSRole.Owner)
        {
            throw new ValidationException(
                "Owner access is required to view grantable users.");
        }

        var taken = await _db.KSGrants.AsNoTracking()
            .Where(g => g.KnowledgeSystemId == ks.Id)
            .Select(g => g.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (ks.OwnerId.HasValue) taken.Add(ks.OwnerId.Value);

        var q = _db.Users.AsNoTracking().Where(u => u.Active);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim()}%";
            q = q.Where(u => EF.Functions.Like(u.Username, like));
        }
        var candidates = await q
            .OrderBy(u => u.Username)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return candidates
            .Where(u => !taken.Contains(u.Id))
            .Select(u => new GrantableUserOut(u.Id, u.Username, u.IsAdmin))
            .ToList();
    }

    /// <summary>
    /// Cross-KS access + recent activity for one user, scoped to the KS
    /// the requester can see. The Python path 404s if the target isn't a
    /// member of <c>this</c> KS &mdash; we mirror that to prevent
    /// user-table enumeration.
    /// </summary>
    public async Task<MemberDetailOut?> MemberDetailAsync(
        Guid ksId, Guid userId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role == KSRole.None) return null;

        var target = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        if (target is null) return null;
        var targetRole = await _access.GetEffectiveRoleAsync(target, ks, _db, ct).ConfigureAwait(false);
        if (targetRole == KSRole.None)
        {
            // Mirror Python: 404 instead of leaking that the user exists
            // but isn't on this KS.
            return null;
        }

        var allKs = await _db.KnowledgeSystems.AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var ksNames = allKs.ToDictionary(k => k.Id, k => k.Name);

        var accessibleKsIds = user.IsAdmin
            ? (HashSet<Guid>?)null
            : await BuildAccessibleKsIdsAsync(user, ct).ConfigureAwait(false);

        var access = new List<MemberAccessEntry>();
        foreach (var k in allKs)
        {
            if (accessibleKsIds is not null && !accessibleKsIds.Contains(k.Id)) continue;
            var r = await _access.GetEffectiveRoleAsync(target, k, _db, ct).ConfigureAwait(false);
            if (r == KSRole.None) continue;
            access.Add(new MemberAccessEntry(k.Id, k.Name, RoleName(r)));
        }

        // Recent activity (30 most-recent audit events for the target).
        var activityQuery = _db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorId == target.Id);
        if (accessibleKsIds is not null)
        {
            activityQuery = activityQuery.Where(e => accessibleKsIds.Contains(e.KnowledgeSystemId));
        }
        var events = await activityQuery.ToListAsync(ct).ConfigureAwait(false);
        events.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        if (events.Count > 30) events.RemoveRange(30, events.Count - 30);
        var activity = events.Select(e => new MemberActivityEntry(
            KsName: ksNames.TryGetValue(e.KnowledgeSystemId, out var n) ? n : "?",
            Action: e.Action,
            Summary: e.Summary,
            CreatedAt: e.CreatedAt)).ToList();

        return new MemberDetailOut(
            User: new MemberDetailUser(
                Id: target.Id,
                Username: target.Username,
                DisplayName: target.DisplayName,
                IsAdmin: target.IsAdmin,
                Active: target.Active),
            Access: access,
            Activity: activity);
    }

    // ----------------------------------------------------------------------
    // Review sidebar counts
    // ----------------------------------------------------------------------

    /// <summary>
    /// Pending-item counts for the Review sidebar badges. Mirrors Python
    /// <c>knowledge.review_counts</c> exactly: open conflicts, pending
    /// entity resolutions, pending terminology proposals, and current
    /// ABox validation error+warning count. The ABox count degrades
    /// gracefully to 0 if the validator can't run (the Python backend
    /// catches the exception so a stale ontology never breaks the badge).
    /// </summary>
    public async Task<ReviewCountsOut?> ReviewCountsAsync(Guid ksId, Actor actor, CancellationToken ct)
    {
        var (user, ks) = await ResolveUserAndKsAsync(ksId, actor, ct).ConfigureAwait(false);
        if (user is null || ks is null) return null;
        var role = await _access.GetEffectiveRoleAsync(user, ks, _db, ct).ConfigureAwait(false);
        if (role == KSRole.None) return null;

        var conflicts = await _db.Conflicts.AsNoTracking()
            .CountAsync(c => c.KnowledgeSystemId == ks.Id && c.Status == "open", ct)
            .ConfigureAwait(false);
        var resolution = await _db.EntityResolutions.AsNoTracking()
            .CountAsync(r => r.KnowledgeSystemId == ks.Id && r.Status == "pending", ct)
            .ConfigureAwait(false);
        var terminology = await _db.TermProposals.AsNoTracking()
            .CountAsync(t => t.KnowledgeSystemId == ks.Id && t.Status == "pending", ct)
            .ConfigureAwait(false);

        // ABox validation count needs the live TBox/ABox graph (Block 6
        // / 7 territory). Until ShapeBuilder + ABoxManager wire in, fall
        // back to 0 — never break the sidebar over a missing counter.
        var validation = 0;

        return ReviewCountsOut.Sum(conflicts, resolution, terminology, validation);
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private async Task<UserEntity?> ResolveUserAsync(Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(actor.UserId, out var userGuid)) return null;
        return await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userGuid, ct)
            .ConfigureAwait(false);
    }

    private async Task<(UserEntity? User, KnowledgeSystemEntity? Ks)> ResolveUserAndKsAsync(
        Guid ksId, Actor actor, CancellationToken ct)
    {
        var user = await ResolveUserAsync(actor, ct).ConfigureAwait(false);
        if (user is null) return (null, null);
        var ks = await _db.KnowledgeSystems
            .FirstOrDefaultAsync(k => k.Id == ksId, ct)
            .ConfigureAwait(false);
        return (user, ks);
    }

    private async Task<HashSet<Guid>> BuildAccessibleKsIdsAsync(UserEntity user, CancellationToken ct)
    {
        var owned = await _db.KnowledgeSystems.AsNoTracking()
            .Where(k => k.OwnerId == user.Id)
            .Select(k => k.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var granted = await _db.KSGrants.AsNoTracking()
            .Where(g => g.UserId == user.Id)
            .Select(g => g.KnowledgeSystemId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var ids = new HashSet<Guid>(owned);
        foreach (var id in granted) ids.Add(id);
        return ids;
    }

    private async Task<IReadOnlyList<KnowledgeSystemOut>> ProjectAsync(
        IReadOnlyList<KnowledgeSystemEntity> rows, UserEntity requester, CancellationToken ct)
    {
        var ksIds = rows.Select(r => r.Id).ToHashSet();
        var ownerIds = rows.Select(r => r.OwnerId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();
        var owners = ownerIds.Count > 0
            ? await _db.Users.AsNoTracking()
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, ct)
                .ConfigureAwait(false)
            : new Dictionary<Guid, UserEntity>();
        var myGrants = await _db.KSGrants.AsNoTracking()
            .Where(g => g.UserId == requester.Id && ksIds.Contains(g.KnowledgeSystemId))
            .ToDictionaryAsync(g => g.KnowledgeSystemId, g => g.Role, ct)
            .ConfigureAwait(false);

        var output = new List<KnowledgeSystemOut>(rows.Count);
        foreach (var ks in rows)
        {
            var role = requester.IsAdmin
                ? KSRole.Owner
                : ks.OwnerId == requester.Id
                    ? KSRole.Owner
                    : myGrants.TryGetValue(ks.Id, out var r) && r is "editor"
                        ? KSRole.Editor
                        : myGrants.TryGetValue(ks.Id, out r) && r is "viewer"
                            ? KSRole.Viewer
                            : KSRole.None;
            output.Add(new KnowledgeSystemOut(
                Id: ks.Id,
                PublicId: ks.PublicId,
                Name: ks.Name,
                Description: ks.Description,
                OwnerId: ks.OwnerId,
                GraphIri: ks.GraphIri,
                BaseIri: ks.BaseIri,
                CreatedAt: ks.CreatedAt,
                UpdatedAt: ks.UpdatedAt,
                ClassCount: ks.ClassCount,
                PropertyCount: ks.PropertyCount,
                AxiomCount: ks.AxiomCount,
                LlmModel: ks.LlmModel,
                LlmProviderId: ks.LlmProviderId,
                EmbeddingProviderId: ks.EmbeddingProviderId,
                EmbeddingModel: ks.EmbeddingModel,
                MyRole: RoleName(role)));
        }
        return output;
    }

    private static string RoleName(KSRole role) => role switch
    {
        KSRole.Owner => "owner",
        KSRole.Editor => "editor",
        KSRole.Viewer => "viewer",
        _ => "viewer",
    };

    private static string? NullIfBlank(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private async Task WriteAuditAsync(
        Guid ksId, UserEntity actor, string action, string summary,
        JsonElement? detail, CancellationToken token)
    {
        // auditevent.legacy_id has a UNIQUE index — every row needs a fresh
        // integer. AllocateAndPersistAsync wraps the alloc + INSERT in a
        // single transaction under the audit_events advisory lock so
        // concurrent WriteAuditAsync calls cannot observe the same
        // MAX+1 and race on the UNIQUE constraint. SQLite is single-writer
        // and falls back to the autocommit path inside the allocator.
        await _allocator.AllocateAndPersistAsync(new AuditEventEntity
        {
            KnowledgeSystemId = ksId,
            ActorId = actor.Id,
            ActorName = actor.DisplayName ?? actor.Username,
            Action = action,
            Summary = summary,
            Detail = detail is null
                ? null
                : JsonDocument.Parse(detail.Value.GetRawText()),
            CreatedAt = _clock.GetUtcNow(),
        }, token).ConfigureAwait(false);
    }

    private static JsonElement? BuildDetail(Guid userId, string? role)
    {
        var payload = role is null
            ? new Dictionary<string, object?> { ["user_id"] = userId }
            : new Dictionary<string, object?> { ["user_id"] = userId, ["role"] = role };
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private async Task DeletePerKsRowsAsync<TEntity>(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, CancellationToken ct)
        where TEntity : class
    {
        var rows = await _db.Set<TEntity>()
            .Where(predicate)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return;
        _db.Set<TEntity>().RemoveRange(rows);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}