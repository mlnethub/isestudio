using System.Diagnostics;
using ISEStudio.Observability;
using ISEStudio.Ontology;
using ISEStudio.Parsing;
using ISEStudio.Storage;
using OntoNamedNode = Oxigraph.NamedNode;
using OntoLiteral = Oxigraph.Literal;
using OntoQuad = Oxigraph.Quad;

namespace ISEStudio.Tests.Observability;

/// <summary>
/// Verifies the Stage 5 Task 3 fix-up round 1 — every helper that wraps a
/// service-layer boundary actually fires at the call site. Each test captures
/// the owning <see cref="ActivitySource"/> with <see cref="TestActivityListener"/>
/// and asserts that the expected activity lands with the canonical name +
/// <c>peer.service</c> + <c>outcome</c> tags.
/// </summary>
/// <remarks>
/// <para>Each test looks up its activity by operation name rather than asserting
/// <c>Assert.Single</c>: parallel test classes (e.g. <c>StoreWrapperTests</c>)
/// can also emit activities on the shared <c>ISEStudio.Rdf</c> source, and
/// the listener captures every event that fires while it is subscribed.
/// Filtering by name keeps the assertion focused on the boundary under test.</para>
/// </remarks>
[Collection("ActivityWrapping")]
public sealed class ActivityWrappingTests
{
    // ----------------------------------------------------------------
    // RDF: StoreWrapper.AddQuads and CaptureAsync
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public void Rdf_store_add_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.RdfSourceName);
        using var store = NewStore();

        var graph = new OntoNamedNode("urn:g1");
        var q = new OntoQuad(new OntoNamedNode("urn:s"), new OntoNamedNode("urn:p"),
            new OntoLiteral("v"), graph);
        store.AddQuads(graph, new[] { q });

        var activity = FindActivity(listener.Snapshot(), "rdf.store.add");
        Assert.NotNull(activity);
        Assert.Equal("oxigraph", activity!.GetTagItem("peer.service"));
        Assert.Equal("urn:g1", activity.GetTagItem("rdf.graph"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
    }

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Rdf_store_capture_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.RdfSourceName);
        using var store = NewStore();

        var graph = new OntoNamedNode("urn:g1");
        await using var capture = await store.CaptureAsync(graph, revertOnError: false);

        var activity = FindActivity(listener.Snapshot(), "rdf.store.capture");
        Assert.NotNull(activity);
        Assert.Equal("oxigraph", activity!.GetTagItem("peer.service"));
        Assert.Equal("urn:g1", activity.GetTagItem("rdf.graph"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
    }

    // ----------------------------------------------------------------
    // RDF: ShaclValidator.Validate
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public void Rdf_shacl_validate_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.RdfSourceName);
        using var shapeStore = NewStore();
        using var dataStore = NewStore();

        // Minimal shape: NodeShape targeting owl:Class with required rdfs:label.
        var shapeGraph = new OntoNamedNode("urn:shapes");
        shapeStore.LoadTurtle(System.Text.Encoding.UTF8.GetBytes(
            """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix owl: <http://www.w3.org/2002/07/owl#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
            @prefix op: <urn:op:> .
            op:OwlClassShape a sh:NodeShape ;
                sh:targetClass owl:Class ;
                sh:property [ sh:path rdfs:label ; sh:minCount 1 ; sh:datatype xsd:string ] .
            """), shapeGraph);

        var validator = new ShaclValidator(shapeStore, dataStore);
        var report = validator.Validate("urn:data");

        var activity = FindActivity(listener.Snapshot(), "rdf.shacl.validate");
        Assert.NotNull(activity);
        Assert.Equal("shacl.validator", activity!.GetTagItem("peer.service"));
        Assert.Equal("urn:data", activity.GetTagItem("rdf.graph"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.NotNull(report);
    }

    // ----------------------------------------------------------------
    // Parsing: DocumentParser.Parse
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public void Parsing_parse_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.ParsingSourceName);

        var parser = new DocumentParser();
        // Parse a tiny plain-text file so the parser exercises its fallback path.
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello world");
        using var ms = new MemoryStream(bytes);
        var result = parser.Parse(ms, "sample.txt");

        var activity = FindActivity(listener.Snapshot(), "parsing.parse");
        Assert.NotNull(activity);
        Assert.Equal("docling", activity!.GetTagItem("peer.service"));
        Assert.Equal("txt", activity.GetTagItem("file.extension"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.NotNull(result);
    }

    // ----------------------------------------------------------------
    // Storage: LocalCasBlobStore.Put
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Storage_localcas_put_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.StorageSourceName);

        var root = Path.Combine(Path.GetTempPath(), "isestudio-activity-" + Guid.NewGuid().ToString("N"));
        var store = new LocalCasBlobStore(root);
        var bytes = "round-trip"u8.ToArray();
        var result = await store.PutAsync(new MemoryStream(bytes), CancellationToken.None);

        var activity = FindActivity(listener.Snapshot(), "storage.localcas.put");
        Assert.NotNull(activity);
        Assert.Equal("minio", activity!.GetTagItem("peer.service"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.NotNull(result);

        try { Directory.Delete(root, recursive: true); } catch { }
    }

    // ----------------------------------------------------------------
    // Storage: WithStorageActivity helper itself emits with peer.service.
    // The MinioBlobStore integration path uses the same helper
    // (LocalCasBlobStore exercises it above), so the helper-level test
    // also pins the activity name + tags the Minio path will produce.
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Storage_with_activity_helper_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.StorageSourceName);

        var bytes = await Telemetry.StorageSource.WithStorageActivity(
            "storage.minio.put",
            bytes: 4L,
            async ct =>
            {
                await Task.Yield();
                return new BlobWriteResult("abcd", "ab/cd/abcd");
            },
            CancellationToken.None);

        var activity = FindActivity(listener.Snapshot(), "storage.minio.put");
        Assert.NotNull(activity);
        Assert.Equal("minio", activity!.GetTagItem("peer.service"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.Equal(4L, activity.GetTagItem("storage.bytes"));
        Assert.NotNull(bytes);
    }

    // ----------------------------------------------------------------
    // MCP: Verify the helper itself emits the expected activity name
    // and tags. The MCP tool surface itself is exercised by the .NET
    // integration test which wires a real DbContext + scope verifier;
    // here we assert that the helper applies the canonical tags when a
    // tool body runs through it.
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Mcp_with_activity_helper_emits_activity_with_peer_service_and_outcome()
    {
        using var listener = TestActivityListener.Capture(Telemetry.McpSourceName);

        var result = await Telemetry.McpSource.WithMcpActivity("sample_tool", async _ =>
        {
            await Task.Yield();
            return new { ok = true };
        }, CancellationToken.None);

        var activity = FindActivity(listener.Snapshot(), "Mcp.Tool.sample_tool");
        Assert.NotNull(activity);
        Assert.Equal("isestudio.mcp", activity!.GetTagItem("peer.service"));
        Assert.Equal("sample_tool", activity.GetTagItem("mcp.tool"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.NotNull(result);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static StoreWrapper NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "isestudio-rdf-" + Guid.NewGuid().ToString("N"));
        return new StoreWrapper(path);
    }

    private static Activity? FindActivity(IReadOnlyList<Activity> activities, string operationName)
    {
        foreach (var activity in activities)
        {
            if (string.Equals(activity.OperationName, operationName, StringComparison.Ordinal))
            {
                return activity;
            }
        }
        return null;
    }
}