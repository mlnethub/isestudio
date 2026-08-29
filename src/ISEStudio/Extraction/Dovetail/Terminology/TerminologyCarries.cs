namespace ISEStudio.Extraction.Dovetail.Terminology;

/// <summary>
/// Per-stage carrier records for the terminology DAG. Dovetail 1.0.0's
/// <c>AddPipelines()</c> generator registers every segment against its
/// interface shape and raises DOVE017 when two segments share one — the
/// three sync passes would otherwise all be
/// <c>IPipelineSegment&lt;TerminologyInput, TermSyncCarry, TermSyncCarry&gt;</c>.
/// Each stage therefore returns a thin marker wrapper around the shared
/// <see cref="TermSyncCarry"/>; steps unwrap via <c>Carry</c> at the
/// delegation boundary, so the deterministic behavior is unchanged
/// (SDD ruling — plan pre-flight checked DOVE006 but missed DOVE017).
/// </summary>
public sealed record EntitySyncCarry(TermSyncCarry Carry);

/// <summary>Stage marker for the alias pass (DOVE017 shape uniqueness).</summary>
public sealed record AliasCarry(TermSyncCarry Carry);

/// <summary>Stage marker for the broader pass (DOVE017 shape uniqueness).</summary>
public sealed record BroaderCarry(TermSyncCarry Carry);
