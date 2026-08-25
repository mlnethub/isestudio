namespace ISEStudio.Application.Foundation;

/// <summary>
/// External / published-API bearer-token principal. The token is checked
/// live on every call (status, KS role, scopes) — the facade never trusts
/// a snapshot captured at issue time. Concrete shape will be filled in by
/// task 4 (MCP transport + live authorization).
/// </summary>
public sealed record TokenPrincipal(
    string TokenId,
    string KnowledgeSystemPublicId,
    IReadOnlyList<string> Scopes);