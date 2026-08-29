using Dovetail;
using ISEStudio.Extraction;
using ISEStudio.Extraction.Dovetail.Terminology.Steps;

namespace ISEStudio.Extraction.Dovetail.Terminology;

/// <summary>
/// Dovetail pipeline that runs the extraction terminology sync as five
/// typed segments: StaleMapping → EntitySync → Alias → Broader → Proposal.
/// Constructed via <see cref="Dovetail.DovetailPipelineBuilderExtensions.AddPipelines"/>;
/// the source generator emits <c>TerminologyPipeline.g.cs</c> with the
/// <see cref="ExecuteAsync"/> method and Mermaid diagram. The orchestrator
/// resolves it from the per-job scope (Slice 3 R2 lifecycle) and falls back
/// to the P1-4 chain (<see cref="TerminologyService.SyncAsync"/> + scoped
/// agent) when it cannot.
/// </summary>
public partial class TerminologyPipeline : IPipeline<TerminologyInput, TerminologyResult>
{
    public TerminologyPipeline(
        [Segment] StaleMappingStep staleMappingStep,
        [Segment] EntitySyncStep entitySyncStep,
        [Segment] AliasStep aliasStep,
        [Segment] BroaderStep broaderStep,
        [Segment] ProposalStep proposalStep)
    {
        StaleMappingStep = staleMappingStep;
        EntitySyncStep = entitySyncStep;
        AliasStep = aliasStep;
        BroaderStep = broaderStep;
        ProposalStep = proposalStep;
    }

    public StaleMappingStep StaleMappingStep { get; }
    public EntitySyncStep EntitySyncStep { get; }
    public AliasStep AliasStep { get; }
    public BroaderStep BroaderStep { get; }
    public ProposalStep ProposalStep { get; }
}
