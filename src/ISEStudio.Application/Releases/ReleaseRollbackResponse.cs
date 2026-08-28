namespace ISEStudio.Application.Releases;

/// <summary>
/// Wire envelope matching <c>ReleaseService.RollbackAsync</c>'s return
/// shape &mdash; <c>{restored:Guid, version:string}</c>. Typed on the
/// application service surface so the dispatcher can pass it through
/// verbatim without re-projection.
/// </summary>
public sealed record ReleaseRollbackResponse(Guid Restored, string Version);