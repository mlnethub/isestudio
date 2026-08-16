namespace OnToPilot.Authentication;

/// <summary>
/// Password hashing and validation, BCrypt-backed. The cost factor matches
/// the Python backend's <c>bcrypt.gensalt()</c> default (12). Password
/// length is bounded by BCrypt's 72-byte UTF-8 input limit; we additionally
/// require 12 characters (a project minimum).
/// </summary>
/// <remarks>
/// <para>Bootstrap validation additionally rejects published example passwords
/// (<c>admin</c>, <c>changeme</c>, …) so empty installs can't accidentally land
/// on a credential that's already in a public password list.</para>
/// </remarks>
public sealed class PasswordService
{
    /// <summary>Minimum password length, mirrored from the Python backend.</summary>
    public const int MinLength = 12;

    /// <summary>BCrypt's UTF-8 byte cap. Inputs beyond this are always rejected.</summary>
    public const int MaxUtf8Bytes = 72;

    /// <summary>Cost factor matched to <c>bcrypt.gensalt()</c>'s default.</summary>
    public const int DefaultCost = 12;

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

    /// <summary>
    /// Hash a freshly chosen password with BCrypt at the default cost.
    /// Caller must have already validated via <see cref="Validate"/>.
    /// </summary>
    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, DefaultCost);

    /// <summary>
    /// Verify a presented password against a stored BCrypt hash. Tolerates
    /// malformed hashes by returning <c>false</c> rather than throwing.
    /// </summary>
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

    /// <summary>
    /// Reject passwords that are too short, exceed BCrypt's UTF-8 byte cap, or
    /// (when <paramref name="bootstrap"/> is <c>true</c>) match a published
    /// example. Existing password hashes are unaffected — this is for newly
    /// seeded, created, or rotated credentials only.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown with a user-facing message describing the rejection reason.
    /// </exception>
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
