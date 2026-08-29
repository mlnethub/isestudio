namespace ISEStudio.Extraction.Dovetail.Job;

/// <summary>
/// Per-phase wrapper records threading <see cref="JobState"/> through the
/// Job DAG. Every phase segment folds the same state, so without a distinct
/// result type per phase all Job segments would share the shape
/// <c>IPipelineSegment&lt;JobState, JobState&gt;</c> and Dovetail 1.0.0
/// refuses to register them (DOVE017: "give each segment a distinct input or
/// result type so their shapes no longer match").
///
/// <para>Same resolution the Slice 4 terminology sub-DAG landed
/// (<c>EntitySyncCarry</c> / <c>AliasCarry</c> / <c>BroaderCarry</c> in
/// <c>TerminologyCarries.cs</c>): one thin wrapper per stage, each holding
/// the shared carry — here <see cref="JobState"/> itself.</para>
/// </summary>
public sealed record TBoxLayerCarry(JobState State);

/// <inheritdoc cref="TBoxLayerCarry"/>
public sealed record CorpusCarry(JobState State);

/// <inheritdoc cref="TBoxLayerCarry"/>
public sealed record HierarchyCarry(JobState State);

/// <inheritdoc cref="TBoxLayerCarry"/>
public sealed record AgentCarry(JobState State);

/// <inheritdoc cref="TBoxLayerCarry"/>
public sealed record ABoxLayerCarry(JobState State);

/// <inheritdoc cref="TBoxLayerCarry"/>
public sealed record TerminologyCarry(JobState State);
