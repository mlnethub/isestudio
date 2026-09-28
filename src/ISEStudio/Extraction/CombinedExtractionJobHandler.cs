using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Parsing;
using ISEStudio.Storage;

namespace ISEStudio.Extraction;

public sealed class CombinedExtractionJobHandler : DurableLayerExtractionJobHandlerBase
{
    private readonly TBoxExtractionService _tbox;
    private readonly ABoxExtractionService _abox;
    private readonly TBoxVerifyService? _verify;
    private readonly CorpusRecoveryService? _corpus;
    private readonly HierarchyRecoveryService? _hierarchy;

    public CombinedExtractionJobHandler(
        Infrastructure.Persistence.ISEStudioDbContext db,
        Llm.IChatClientFactory chatFactory,
        ExtractionJobStore jobs,
        JobPipelineRouter router,
        PromptSnapshotService promptSnapshot,
        TBoxExtractionService tbox,
        ABoxExtractionService abox,
        IBlobStore blobs,
        IDocumentParser parser,
        Chunker chunker,
        TBoxVerifyService? verify = null,
        CorpusRecoveryService? corpus = null,
        HierarchyRecoveryService? hierarchy = null)
        : base(db, chatFactory, jobs, router, promptSnapshot, blobs, parser, chunker)
    {
        _tbox = tbox;
        _abox = abox;
        _verify = verify;
        _corpus = corpus;
        _hierarchy = hierarchy;
    }

    public override string Kind => ExtractionWire.KindBoth;

    protected override JobKind JobKind => JobKind.Combined;

    protected override IReadOnlyDictionary<string, string> BuildPromptMap()
    {
        var prompts = new Dictionary<string, string>
        {
            [TBoxExtractionService.PromptKey] = _tbox.ResolveSystemPrompt(),
            [ABoxExtractionService.PromptKey] = _abox.ResolveSystemPrompt(),
        };

        if (_verify is not null)
        {
            prompts[TBoxVerifyService.BoundaryCriticKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.BoundaryCriticKey);
            prompts[TBoxVerifyService.BoundaryAdjudicatorKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.BoundaryAdjudicatorKey);
            prompts[TBoxVerifyService.DenotationCriticKey] =
                _verify.ResolveSystemPrompt(TBoxVerifyService.DenotationCriticKey);
        }

        if (_corpus is not null)
        {
            prompts[CorpusRecoveryService.EvidenceSelectorKey] =
                _corpus.ResolveSystemPrompt(CorpusRecoveryService.EvidenceSelectorKey);
            prompts[CorpusRecoveryService.CorpusRecoveryKey] =
                _corpus.ResolveSystemPrompt(CorpusRecoveryService.CorpusRecoveryKey);
        }

        if (_hierarchy is not null)
        {
            prompts[HierarchyRecoveryService.HierarchyCriticKey] =
                _hierarchy.ResolveSystemPrompt(HierarchyRecoveryService.HierarchyCriticKey);
            prompts[HierarchyRecoveryService.HierarchyRecoveryKey] =
                _hierarchy.ResolveSystemPrompt(HierarchyRecoveryService.HierarchyRecoveryKey);
        }

        return prompts;
    }
}
