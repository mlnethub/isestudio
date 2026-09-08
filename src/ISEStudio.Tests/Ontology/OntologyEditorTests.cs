using ISEStudio.Ontology;

namespace ISEStudio.Tests.Ontology;

public sealed class OntologyEditorTests
{
    [Fact]
    public async Task ApplyEditAsync_ForwardsGraphAndOperationToRepository()
    {
        var repository = new RecordingOntologyRepository();
        var editor = new OntologyEditor(repository);
        var operation = new Dictionary<string, object?> { ["op"] = "add_class", ["label"] = "Person" };

        var result = await editor.ApplyEditAsync("urn:graph", "urn:base#", operation);

        Assert.Equal("urn:base#Person", result);
        Assert.Equal("urn:graph", repository.GraphIri);
        Assert.Equal("urn:base#", repository.BaseIri);
        Assert.Equal("add_class", repository.Operation);
    }

    [Fact]
    public async Task ApplyEditAsync_RejectsMissingOperation()
    {
        var editor = new OntologyEditor(new RecordingOntologyRepository());

        await Assert.ThrowsAsync<OntologyEditException>(async () =>
            await editor.ApplyEditAsync("urn:graph", "urn:base#", new Dictionary<string, object?>()));
    }

    private sealed class RecordingOntologyRepository : IOntologyRepository
    {
        public string? GraphIri { get; private set; }
        public string? BaseIri { get; private set; }
        public string? Operation { get; private set; }

        public Task<string> ApplyEditAsync(string graphIri, string baseIri, string operation, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken = default)
        {
            GraphIri = graphIri;
            BaseIri = baseIri;
            Operation = operation;
            return Task.FromResult("urn:base#Person");
        }
    }
}
