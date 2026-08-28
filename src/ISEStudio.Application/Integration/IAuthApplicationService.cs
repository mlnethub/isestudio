using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the five admin-side <c>auth.*</c>
/// dispatcher arms (12/13 slice): update_me / list_users /
/// create_user / update_user / delete_user. (<c>auth.login</c> /
/// <c>logout</c> / <c>me</c> stay inline in the controller — they own
/// the session-cookie plumbing — so the dispatcher keeps those three
/// one-line stubs untouched.)
///
/// <para>Returns are <c>object?</c> because the wire projection of
/// <c>UserOut</c> is anonymous (snake_case envelope). A <c>null</c>
/// return degrades to the dispatcher's schema-compatible fallback per
/// arm; a missing body throws <see cref="InvalidOperationException"/>
/// exactly like the pre-split helpers did.</para>
/// </summary>
public interface IAuthApplicationService
{
    /// <summary><c>auth.update_me</c> — body <c>{display_name, ...}</c>, user id from Actor.</summary>
    Task<object?> UpdateMeAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>auth.list_users</c> — all users, admin side.</summary>
    Task<object?> ListUsersAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>auth.create_user</c> — body <c>{username, password, display_name, is_admin}</c>.</summary>
    Task<object?> CreateUserAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>auth.update_user</c> — user Guid in <c>ResourceId</c>, patch body.</summary>
    Task<object?> UpdateUserAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>auth.delete_user</c> — user Guid in <c>ResourceId</c>.</summary>
    Task<object?> DeleteUserAsync(InternalRequest request, CancellationToken cancellationToken);
}
