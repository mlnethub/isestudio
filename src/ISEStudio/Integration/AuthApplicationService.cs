using ISEStudio.Application.Foundation;
using ISEStudio.Application.Integration;
using ISEStudio.Authentication;
using static ISEStudio.Integration.InternalRequestHelpers;

namespace ISEStudio.Integration;

/// <summary>
/// Application service for the five admin-side <c>auth.*</c> dispatcher
/// arms (12/13 slice): update_me / list_users / create_user /
/// update_user / delete_user. Delegates to the scoped
/// <see cref="AuthService"/> and owns the snake_case <c>UserOut</c>
/// wire projection. Missing body throws
/// <see cref="InvalidOperationException"/>; an unparsable user id
/// returns <c>null</c> so the dispatcher degrades to the per-arm
/// schema-compatible fallback — both matching the pre-split helpers.
/// </summary>
public sealed class AuthApplicationService : IAuthApplicationService
{
    private readonly AuthService _auth;

    public AuthApplicationService(AuthService auth)
    {
        _auth = auth;
    }

    public async Task<object?> UpdateMeAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<UpdateMeRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for auth.update_me.");
        var userId = Guid.TryParse(request.Actor.UserId, out var parsed)
            ? parsed : Guid.Empty;
        if (userId == Guid.Empty) return null;
        var row = await _auth.UpdateMeAsync(userId, body, ct).ConfigureAwait(false);
        return (object?)ProjectUserOut(row);
    }

    public async Task<object?> ListUsersAsync(
        InternalRequest request, CancellationToken ct)
    {
        var rows = await _auth.ListUsersAsync(ct).ConfigureAwait(false);
        return (object?)rows.Select(ProjectUserOut).ToArray();
    }

    public async Task<object?> CreateUserAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<CreateUserRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for auth.create_user.");
        var row = await _auth.CreateUserAsync(body, ct).ConfigureAwait(false);
        return (object?)ProjectUserOut(row);
    }

    public async Task<object?> UpdateUserAsync(
        InternalRequest request, CancellationToken ct)
    {
        var body = DeserializeBody<UpdateUserRequest>(request)
            ?? throw new InvalidOperationException(
                "Request body is required for auth.update_user.");
        if (!Guid.TryParse(request.ResourceId, out var userId)) return null;
        var row = await _auth.UpdateUserAsync(userId, body, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)ProjectUserOut(row);
    }

    public async Task<object?> DeleteUserAsync(
        InternalRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.ResourceId, out var userId)) return null;
        var deleted = await _auth.DeleteUserAsync(userId, request.Actor, ct)
            .ConfigureAwait(false);
        return (object?)new { deleted = deleted };
    }

    private static object ProjectUserOut(UserOut row) => new
    {
        id = row.Id,
        username = row.Username,
        display_name = row.DisplayName,
        is_admin = row.IsAdmin,
        active = row.Active,
    };
}
