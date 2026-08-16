namespace OnToPilot.Authentication;

/// <summary>
/// Password hashing and verification. <see cref="PasswordService"/> is the
/// BCrypt-backed production implementation; tests substitute a spy to verify
/// the controller invokes verification on every login attempt (so a missing
/// user and a wrong password cost the same time).
/// </summary>
public interface IPasswordService
{
    /// <summary>Hash a freshly chosen password. Caller must have validated via <see cref="Validate"/>.</summary>
    string Hash(string password);

    /// <summary>Verify a presented password against a stored BCrypt hash.</summary>
    bool Verify(string password, string passwordHash);

    /// <summary>Reject passwords that violate length, byte-cap, or bootstrap rules.</summary>
    void Validate(string password, bool bootstrap = false);
}

/// <summary>
/// BCrypt-backed password hashing and validation. The cost factor matches
/// the Python backend's <c>bcrypt.gensalt()</c> default (12). Password
/// length is bounded by BCrypt's 72-byte UTF-8 input limit; we additionally
/// require 12 characters (a project minimum).
/// </summary>
/// <remarks>
/// <para>Bootstrap validation additionally rejects published example passwords
/// (<c>admin</c>, <c>changeme</c>, …) so empty installs can't accidentally land
/// on a credential that's already in a public password list.</para>
/// <para>
/// <see cref="TimingSafeDummyHash"/> is a precomputed BCrypt hash the login
/// controller feeds to <see cref="Verify"/> when the username doesn't exist,
/// so the work done on a missing user matches the work done on a wrong
/// password and the response time can't leak username existence.
/// </para>
/// </remarks>
public sealed class PasswordService : IPasswordService
{
    /// <summary>Minimum password length, mirrored from the Python backend.</summary>
    public const int MinLength = 12;

    /// <summary>BCrypt's UTF-8 byte cap. Inputs beyond this are always rejected.</summary>
    public const int MaxUtf8Bytes = 72;

    /// <summary>Cost factor matched to <c>bcrypt.gensalt()</c>'s default.</summary>
    public const int DefaultCost = 12;

    /// <summary>
    /// A valid BCrypt hash generated once at startup with the same cost factor
    /// as real hashes. Used by the login path to equalize verification timing
    /// when the presented username doesn't exist.
    /// </summary>
    public static readonly string TimingSafeDummyHash =
        BCrypt.Net.BCrypt.HashPassword("timing-safe-dummy-password", DefaultCost);

    /// <summary>
    /// Bootstrap-time reject list. Empty installs MUST NOT land on one of
    /// these — they're publicly documented and add zero protection.
    /// </summary>
    private static readonly HashSet<string> BootstrapRejects = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "admin123",
        "change-me",
        "changeme",
        "password",
        "replace-with-a-strong-password",
    };

    /// <inheritdoc />
    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, DefaultCost);

    /// <inheritdoc />
    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(passwordHash)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Validate(string password, bool bootstrap = false)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (bootstrap && BootstrapRejects.Contains(password.Trim()))
        {
            throw new ArgumentException(
                "ADMIN_PASSWORD must not use a published example or common default");
        }
        if (password.Length < MinLength)
        {
            throw new ArgumentException(
                $"Password must be at least {MinLength} characters");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(password) > MaxUtf8Bytes)
        {
            throw new ArgumentException(
                $"Password must be at most {MaxUtf8Bytes} UTF-8 bytes");
        }
    }
}