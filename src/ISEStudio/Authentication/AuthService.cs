using Microsoft.EntityFrameworkCore;
using ISEStudio.Api;
using ISEStudio.Application.Foundation;
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using ISEStudio.Ontology;

namespace ISEStudio.Authentication;

// ---------------------------------------------------------------------------
// Wire DTOs for /api/auth/* (admin user CRUD). Mirrors
// backend/app/api/auth.py:UpdateMe / CreateUser / UpdateUser so the
// existing frontend UserOut / settings dialog types stay in lock-step.
// ---------------------------------------------------------------------------

/// <summary>
/// Body for <c>PATCH /api/auth/me</c>. Mirrors
/// <c>backend/app/api/auth.py:UpdateMe</c>: <c>display_name</c> sets or
/// clears the nickname; changing the password requires
/// <c>current_password</c> + a new password that satisfies the password
/// validation rules.
/// </summary>
public sealed record UpdateMeRequest(
    string? DisplayName,
    string? CurrentPassword,
    string? NewPassword);

/// <summary>Body for <c>POST /api/auth/users</c>. Mirrors Python <c>CreateUser</c>.</summary>
public sealed record CreateUserRequest(
    string Username,
    string Password,
    bool IsAdmin = false);

/// <summary>
/// Body for <c>PATCH /api/auth/users/{uid}</c>. Each field is three-valued:
/// omit (null) = unchanged; non-null = replace. Mirrors Python
/// <c>UpdateUser</c>.
/// </summary>
public sealed record UpdateUserRequest(
    string? Password,
    bool? IsAdmin,
    bool? Active);

/// <summary>
/// Self-service + admin user CRUD. Replaces the placeholder
/// <c>auth.update_me</c>, <c>auth.list_users</c>, <c>auth.create_user</c>,
/// <c>auth.update_user</c>, and <c>auth.delete_user</c> dispatcher arms.
/// The login / logout / me arms stay inline in <see cref="Controllers.AuthController"/>
/// because they own the AuthSessionEntity + opaque-cookie plumbing; the
/// admin-side CRUD goes through the dispatcher just like every other slice.
///
/// <para>The role guards mirror Python <c>backend/app/api/auth.py</c>:
/// deactivating or deleting the last admin is rejected (would lock the
/// operator out mid-request); deactivating yourself is rejected
/// (same reason); deleting a user that owns a KS is rejected (the
/// transfer-or-delete-the-KS-first rule).</para>
/// </summary>
public sealed class AuthService
{
    private readonly ISEStudioDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly TimeProvider _clock;
    private readonly LegacyIdAllocator _allocator;

    public AuthService(
        ISEStudioDbContext db,
        IPasswordService passwords,
        TimeProvider clock,
        LegacyIdAllocator allocator)
    {
        _db = db;
        _passwords = passwords;
        _clock = clock;
        _allocator = allocator;
    }

    /// <summary>
    /// Self-service profile update. Mirrors Python <c>update_me</c>.
    /// </summary>
    public async Task<UserOut> UpdateMeAsync(
        Guid userId, UpdateMeRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new KeyNotFoundException($"User {userId} not found.");
        }

        if (body.DisplayName is not null)
        {
            var trimmed = body.DisplayName.Trim();
            user.DisplayName = trimmed.Length == 0 ? null : trimmed;
        }

        if (body.NewPassword is not null)
        {
            if (string.IsNullOrEmpty(body.CurrentPassword)
                || !_passwords.Verify(body.CurrentPassword, user.PasswordHash))
            {
                throw new ValidationException("Current password is incorrect");
            }
            _passwords.Validate(body.NewPassword);
            user.PasswordHash = _passwords.Hash(body.NewPassword);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UserOut.From(user);
    }

    /// <summary>List every user, ordered by <c>Id</c>. Mirrors Python <c>list_users</c>.</summary>
    public async Task<IReadOnlyList<UserOut>> ListUsersAsync(CancellationToken ct)
    {
        var rows = await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(UserOut.From);
    }

    /// <summary>Create a new user. Mirrors Python <c>create_user</c>.</summary>
    public async Task<UserOut> CreateUserAsync(
        CreateUserRequest body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var username = body.Username.Trim();
        if (username.Length == 0)
        {
            throw new ValidationException("Username is required");
        }
        _passwords.Validate(body.Password);

        var existing = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ResourceInUseException("Username already exists");
        }

        var entity = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = null,
            PasswordHash = _passwords.Hash(body.Password),
            IsAdmin = body.IsAdmin,
            Active = true,
            CreatedAt = _clock.GetUtcNow(),
        };
        // AllocateAndPersistAsync assigns LegacyId under the per-table
        // advisory lock + writes the row; without it a second CreateUser
        // in the same DbContext lifetime 500s with UNIQUE-constraint
        // violation on ux_user_legacy_id.
        await _allocator.AllocateAndPersistAsync(entity, ct).ConfigureAwait(false);
        return UserOut.From(entity);
    }

    /// <summary>Update an existing user. Mirrors Python <c>update_user</c>.</summary>
    public async Task<UserOut> UpdateUserAsync(
        Guid userId, UpdateUserRequest body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var entity = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException("User not found");
        }

        var actorId = Guid.TryParse(actor.UserId, out var parsed) ? parsed : Guid.Empty;

        // Mirrors Python:
        //  - "Can't remove the last admin" (would lock the operator out).
        //  - "You can't deactivate yourself".
        if (body.IsAdmin == false && entity.IsAdmin)
        {
            var otherAdmins = await _db.Users
                .AsNoTracking()
                .Where(u => u.IsAdmin && u.Id != userId)
                .AnyAsync(ct)
                .ConfigureAwait(false);
            if (!otherAdmins)
            {
                throw new ValidationException("Can't remove the last admin");
            }
        }
        if (body.Active == false && entity.Id == actorId)
        {
            throw new ValidationException("You can't deactivate yourself");
        }

        if (body.Password is not null)
        {
            _passwords.Validate(body.Password);
            entity.PasswordHash = _passwords.Hash(body.Password);
        }
        if (body.IsAdmin is not null) entity.IsAdmin = body.IsAdmin.Value;
        if (body.Active is not null)
        {
            entity.Active = body.Active.Value;
            if (body.Active == false)
            {
                // Revoke live sessions + MCP tokens on deactivation
                // (matches the Python update_user branch).
                var sessions = await _db.AuthSessions
                    .Where(s => s.UserId == userId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                _db.AuthSessions.RemoveRange(sessions);

                var tokens = await _db.McpUserTokens
                    .Where(t => t.UserId == userId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                _db.McpUserTokens.RemoveRange(tokens);
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UserOut.From(entity);
    }

    /// <summary>
    /// Delete a user. Mirrors Python <c>delete_user</c>: refuses to
    /// delete the actor themselves, refuses to delete a user that still
    /// owns a KS (transfer-or-delete rule), cascades through
    /// <see cref="KSGrantEntity"/> / <see cref="AuthSessionEntity"/> /
    /// <see cref="McpUserTokenEntity"/> so no orphan rows survive.
    /// </summary>
    /// <returns>The deleted user's id; throws when the user owns a KS or
    /// tries to self-delete.</returns>
    public async Task<Guid> DeleteUserAsync(
        Guid userId, Actor actor, CancellationToken ct)
    {
        var entity = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException("User not found");
        }

        var actorId = Guid.TryParse(actor.UserId, out var parsed) ? parsed : Guid.Empty;
        if (entity.Id == actorId)
        {
            throw new ValidationException("You can't delete yourself");
        }

        var owned = await _db.KnowledgeSystems
            .AsNoTracking()
            .Where(k => k.OwnerId == userId)
            .Select(k => new { k.Id, k.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (owned.Count > 0)
        {
            var names = string.Join("、",
                owned.Take(5).Select(k => k.Name));
            throw new ResourceInUseException(
                $"This user owns {owned.Count} knowledge system(s) ({names}…); " +
                "transfer or delete them before deleting the user");
        }

        // Cascade through grants / sessions / MCP tokens so no orphan
        // rows survive the user deletion.
        var grants = await _db.KSGrants
            .Where(g => g.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _db.KSGrants.RemoveRange(grants);

        var sessions = await _db.AuthSessions
            .Where(s => s.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _db.AuthSessions.RemoveRange(sessions);

        var tokens = await _db.McpUserTokens
            .Where(t => t.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _db.McpUserTokens.RemoveRange(tokens);

        _db.Users.Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.Id;
    }
}