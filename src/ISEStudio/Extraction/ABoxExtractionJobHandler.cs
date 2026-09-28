using ISEStudio.Extraction.Dovetail.Job;
using ISEStudio.Parsing;
using ISEStudio.Storage;

namespace ISEStudio.Extraction;

public sealed class ABoxExtractionJobHandler : DurableLayerExtractionJobHandlerBase
{
    private readonly ABoxExtractionService _abox;

    public ABoxExtractionJobHandler(
        Infrastructure.Persistence.ISEStudioDbContext db,
        Llm.IChatClientFactory chatFactory,
        ExtractionJobStore jobs,
        JobPipelineRouter router,
        PromptSnapshotService promptSnapshot,
        ABoxExtractionService abox,
        IBlobStore blobs,
        IDocumentParser parser,
        Chunker chunker)
        : base(db, chatFactory, jobs, router, promptSnapshot, blobs, parser, chunker)
    {
        _abox = abox;
    }

    public override string Kind => ExtractionWire.KindABox;

    protected override JobKind JobKind => JobKind.ABoxOnly;

    protected override IReadOnlyDictionary<string, string> BuildPromptMap()
        => new Dictionary<string, string>
        {
            [ABoxExtractionService.PromptKey] = _abox.ResolveSystemPrompt(),
        };
}