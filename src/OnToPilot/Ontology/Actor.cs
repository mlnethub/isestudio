namespace OnToPilot.Ontology;

/// <summary>
/// Identifies the principal performing an ontology-level operation. Mirrors
/// the audit-trail columns on <c>OntologyReleaseEntity</c> /
/// <c>ExportJobEntity</c>; the full user lookup is downstream of this type
/// so tests can construct an actor inline.
/// </summary>
public sealed record Actor(string UserId, string? DisplayName = null);