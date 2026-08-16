using Microsoft.EntityFrameworkCore;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;

namespace OnToPilot.Authorization;

/// <summary>
/// Effective role for <c>(user, knowledge system)</c>, plus a hierarchy
/// check so callers can require a minimum level
/// (<c>Viewer &lt; Editor &lt; Owner</c>). Admins are equivalent to owners on
/// a per-KS basis, matching the Python backend's
/// <c>app.permissions.effective_role</c>.
/// </summary>
public enum KSRole
{
    /// <summary>No access — no grant and not an admin / owner.</summary>
    None = 0,

    /// <summary>Read access.</summary>
    Viewer = 1,

    /// <summary>Content-mutation access (extract, edit).</summary>
    Editor = 2,

    /// <summary>Manage / delete + everything below. Admins resolve here.</summary>
    Owner = 3,
}

/// <summary>
/// Computes a user's effective role on a single knowledge system and answers
/// hierarchy queries against it.
/// </summary>
public sealed class KnowledgeSystemAccessService
{
    /// <summary>
    /// Return the highest role the user holds against this KS:
    /// <list type="bullet">
    ///   <item>admins ⇒ <see cref="KSRole.Owner"/></item>
    ///   <item>KS.owner_id == user.id ⇒ <see cref="KSRole.Owner"/></item>
    ///   <item>an explicit grant row ⇒ <see cref="KSRole.Viewer"/> or <see cref="KSRole.Editor"/></item>
    ///   <item>otherwise <see cref="KSRole.None"/></item>
    /// </list>
    /// </summary>
    public async Task<KSRole> GetEffectiveRoleAsync(
        UserEntity user,
        KnowledgeSystemEntity ks,
        OnToPilotDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(ks);
        ArgumentNullException.ThrowIfNull(db);

        if (user.IsAdmin) return KSRole.Owner;
        if (ks.OwnerId == user.Id) return KSRole.Owner;

        var grantRole = await db.KSGrants
            .Where(g => g.KnowledgeSystemId == ks.Id && g.UserId == user.Id)
            .Select(g => g.Role)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return grantRole switch
        {
            "viewer" => KSRole.Viewer,
            "editor" => KSRole.Editor,
            _ => KSRole.None,
        };
    }

    /// <summary>
    /// True when the user's effective role on the KS meets or exceeds
    /// <paramref name="minimum"/>. <see cref="KSRole.Owner"/> (which admins
    /// also resolve to) satisfies every requirement.
    /// </summary>
    public async Task<bool> HasAtLeastAsync(
        UserEntity user,
        KnowledgeSystemEntity ks,
        KSRole minimum,
        OnToPilotDbContext db,
        CancellationToken cancellationToken)
    {
        var role = await GetEffectiveRoleAsync(user, ks, db, cancellationToken).ConfigureAwait(false);
        return role >= minimum;
    }
}
