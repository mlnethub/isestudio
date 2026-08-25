namespace ISEStudio.Application.Foundation;

/// <summary>
/// Identifies the principal performing an ontology-level operation. Mirrors
/// the audit-trail columns on the workspace release / export-job entities;
/// the full user lookup is downstream of this type so callers can construct
/// an actor inline. Moved from <c>ISEStudio.Ontology</c> so the shared
/// <see cref="Integration.IIntegrationApiFacade"/> surface does not have to
/// reference the web project.
/// </summary>
public sealed record Actor(string UserId, string? DisplayName = null);