namespace OnToPilot.Authorization;

/// <summary>
/// Named authorization policies registered in <c>Program.cs:544</c>
/// via <c>AddAuthorization</c>. Use these constants instead of inline
/// policy name strings to avoid typos.
/// </summary>
public static class Policies
{
    /// <summary>Global admin-only endpoints (settings, providers, users).</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Hook for per-KS Owner-only operations; currently Admin-only
    /// (full KSRole-aware enforcement is a Step 4 follow-up).</summary>
    public const string KSOwnerOnly = "KSOwnerOnly";
}
