using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Authentication;

/// <summary>
/// Public user view. Mirrors the Python backend's <c>UserOut</c> shape
/// (id, username, display_name, is_admin, active) so existing tooling keeps
/// working during the .NET migration. The DTO lives in
/// <c>ISEStudio.Authentication</c> rather than <c>ISEStudio.Controllers</c>
/// so the dispatcher + AuthService can share it without taking a
/// dependency on the Controllers namespace (which would otherwise be a
/// layering inversion: Integration → Controllers).
/// </summary>
public sealed class UserOut
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsAdmin { get; set; }
    public bool Active { get; set; }

    public static UserOut From(UserEntity u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        IsAdmin = u.IsAdmin,
        Active = u.Active,
    };
}