using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Observability;
using OnToPilot.Ontology;
using OnToPilot.Parsing;
using Serilog.Events;
using Serilog.Parsing;

namespace OnToPilot.Tests.Observability;

/// <summary>
/// Verifies the Stage 5 Task 3 observability surface:
///
/// <list type="bullet">
///   <item><see cref="Telemetry"/> exposes the five named
///     <see cref="ActivitySource"/>s and the shared
///     <see cref="System.Diagnostics.Metrics.Meter"/>.</item>
///   <item>The LLM extraction services emit an <c>Llm.Extract</c>
///     activity tagged with the provider / model but never with a key,
///     prompt, or document body.</item>
///   <item><see cref="SecretRedactionProcessor"/> scrubs API keys,
///     bearer tokens, session tokens, prompts, and document bodies from
///     log events — including nested dictionary / sequence values.</item>
/// </list>
/// </summary>
[Collection("ActivityWrapping")]
public sealed class TelemetryTests
{
    private const string Provider = "fake";
    private const string Model = "fake-1";

    private static TBoxExtractionService Service { get; } =
        new(Options.Create(new OnToPilotOptions()));

    // The "Request" placeholder from the brief test maps to the (chat, ks,
    // chunk) tuple the extraction services consume — same intent, same
    // payload, adapted to the existing API surface.
    private static (IChatClient chat, KsContext ks, ChunkSpan chunk) Request { get; } =
        (CreateFakeChat(), new KsContext("http://isestudio.test/ks/1", "http://isestudio.test/base/"),
            new ChunkSpan(Idx: 0, Text: "Pump is a kind of device.", CharStart: 0, CharEnd: 26, TokenEstimate: 8));

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Llm_activity_records_provider_without_secret_or_prompt()
    {
        using var listener = TestActivityListener.Capture(Telemetry.LlmSourceName);

        var delta = await Service.ExtractAsync(Request.chat, Request.ks, Request.chunk, CancellationToken.None);

        var activity = Assert.Single(listener.Snapshot());
        Assert.Equal("Llm.Extract", activity.OperationName);
        Assert.Equal(Provider, activity.GetTagItem("llm.provider"));
        Assert.Equal(Model, activity.GetTagItem("llm.model"));
        Assert.DoesNotContain(activity.TagObjects,
            tag => tag.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        // The chunk text must NOT leak onto a tag either — only the chunk
        // index is safe to surface.
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Value is string s && s.Contains("Pump"));
        Assert.NotNull(delta);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void All_source_names_are_registered()
    {
        Assert.Equal(5, Telemetry.AllSourceNames.Count);
        Assert.Contains(Telemetry.LlmSourceName, Telemetry.AllSourceNames);
        Assert.Contains(Telemetry.RdfSourceName, Telemetry.AllSourceNames);
        Assert.Contains(Telemetry.ParsingSourceName, Telemetry.AllSourceNames);
        Assert.Contains(Telemetry.StorageSourceName, Telemetry.AllSourceNames);
        Assert.Contains(Telemetry.McpSourceName, Telemetry.AllSourceNames);

        // Each source is non-null and has a non-empty name.
        Assert.Equal("OnToPilot.Llm", Telemetry.LlmSource.Name);
        Assert.Equal("OnToPilot.Rdf", Telemetry.RdfSource.Name);
        Assert.Equal("OnToPilot.Parsing", Telemetry.ParsingSource.Name);
        Assert.Equal("OnToPilot.Storage", Telemetry.StorageSource.Name);
        Assert.Equal("OnToPilot.Mcp", Telemetry.McpSource.Name);

        // The shared meter is exposed under "OnToPilot" so the
        // .WithMetrics(m => m.AddMeter("OnToPilot")) call in Program.cs
        // picks up every counter / histogram defined here.
        Assert.Equal("OnToPilot", Telemetry.Meter.Name);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Extractions_counters_and_histogram_are_registered()
    {
        Assert.Equal("ontopilot.extraction.started", Telemetry.ExtractionsStarted.Name);
        Assert.Equal("{chunk}", Telemetry.ExtractionsStarted.Unit);
        Assert.Equal("ontopilot.extraction.completed", Telemetry.ExtractionsCompleted.Name);
        Assert.Equal("ontopilot.extraction.duration", Telemetry.ExtractionDuration.Name);
        Assert.Equal("ms", Telemetry.ExtractionDuration.Unit);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Llm_activity_records_outcome_and_duration_on_success()
    {
        using var listener = TestActivityListener.Capture(Telemetry.LlmSourceName);

        await Service.ExtractAsync(Request.chat, Request.ks, Request.chunk, CancellationToken.None);

        var activity = Assert.Single(listener.Snapshot());
        Assert.Equal("success", activity.GetTagItem("outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public async Task Llm_activity_records_error_on_failure()
    {
        using var listener = TestActivityListener.Capture(Telemetry.LlmSourceName);
        var failingChat = CreateFailingChat(new InvalidOperationException("provider exploded"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ExtractAsync(failingChat, Request.ks, Request.chunk, CancellationToken.None));

        Assert.Equal("provider exploded", thrown.Message);
        var activity = Assert.Single(listener.Snapshot());
        Assert.Equal("error", activity.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    // ----------------------------------------------------------------
    // Secret redaction
    // ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_replaces_top_level_secret_string()
    {
        var evt = BuildEvent(("api_key", "sk-1234567890"), ("UserName", "alice"));
        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder,
            ((ScalarValue)evt.Properties["api_key"]).Value);
        Assert.Equal("alice", ((ScalarValue)evt.Properties["UserName"]).Value);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_walks_dictionary_and_structure_values()
    {
        var inner = new StructureValue(new[]
        {
            new LogEventProperty("bearer_token", new ScalarValue("sek")),
            new LogEventProperty("UserName", new ScalarValue("bob")),
        });
        var dict = new DictionaryValue(new[]
        {
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("session_token"), new ScalarValue("xyz")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("payload"), inner),
        });
        var evt = BuildEvent(("request", dict), ("description", "leave alone"));

        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        var request = Assert.IsType<DictionaryValue>(evt.Properties["request"]);
        var sessionEntry = request.Elements.Single(e => e.Key.Value as string == "session_token");
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder, ((ScalarValue)sessionEntry.Value).Value);

        var innerAfter = Assert.IsType<StructureValue>(request.Elements
            .Single(e => e.Key.Value as string == "payload").Value);
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder,
            ((ScalarValue)innerAfter.Properties.Single(p => p.Name == "bearer_token").Value).Value);
        Assert.Equal("bob",
            ((ScalarValue)innerAfter.Properties.Single(p => p.Name == "UserName").Value).Value);

        Assert.Equal("leave alone", ((ScalarValue)evt.Properties["description"]).Value);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_does_not_touch_business_fields()
    {
        var evt = BuildEvent(
            ("UserId", "abc"),
            ("Email", "alice@example.com"),
            ("endpoint", "https://api.openai.com/v1"),
            ("provider", "openai"));

        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        Assert.Equal("abc", ((ScalarValue)evt.Properties["UserId"]).Value);
        Assert.Equal("alice@example.com", ((ScalarValue)evt.Properties["Email"]).Value);
        Assert.Equal("https://api.openai.com/v1", ((ScalarValue)evt.Properties["endpoint"]).Value);
        Assert.Equal("openai", ((ScalarValue)evt.Properties["provider"]).Value);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_redacts_document_body_and_prompt_fields()
    {
        var documentBody = "very secret thing the user uploaded";
        var evt = BuildEvent(
            ("document_body", documentBody),
            ("system_prompt", "you are a helpful assistant"),
            ("user_id", "alice"));

        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder,
            ((ScalarValue)evt.Properties["document_body"]).Value);
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder,
            ((ScalarValue)evt.Properties["system_prompt"]).Value);
        Assert.Equal("alice", ((ScalarValue)evt.Properties["user_id"]).Value);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_keyword_match_is_case_insensitive()
    {
        var evt = BuildEvent(
            ("API_KEY", "sk-1"),
            ("Bearer", "Bearer abc"),
            ("Session_Token", "tok"),
            ("SECRET", "shh"));

        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder, ((ScalarValue)evt.Properties["API_KEY"]).Value);
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder, ((ScalarValue)evt.Properties["Bearer"]).Value);
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder, ((ScalarValue)evt.Properties["Session_Token"]).Value);
        Assert.Equal(SecretRedactionProcessor.RedactedPlaceholder, ((ScalarValue)evt.Properties["SECRET"]).Value);
    }

    [Fact]
    [Trait("Category", "Observability")]
    public void Redactor_allowlist_exempts_safe_counters()
    {
        var evt = BuildEvent(
            ("bearer_count", 3),
            ("tokens_per_minute", 120),
            ("secret_count", 0));

        new SecretRedactionProcessor().Enrich(evt, new SimplePropertyFactory());

        Assert.Equal(3, ((ScalarValue)evt.Properties["bearer_count"]).Value);
        Assert.Equal(120, ((ScalarValue)evt.Properties["tokens_per_minute"]).Value);
        Assert.Equal(0, ((ScalarValue)evt.Properties["secret_count"]).Value);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static LogEvent BuildEvent(params (string Name, object Value)[] properties)
    {
        var template = new MessageTemplateParser().Parse("{Properties}");
        var props = new List<LogEventProperty>();
        foreach (var (name, value) in properties)
        {
            var propertyValue = value is LogEventPropertyValue lev
                ? lev
                : new ScalarValue(value);
            props.Add(new LogEventProperty(name, propertyValue));
        }
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            template,
            props);
    }

    private static IChatClient CreateFakeChat()
    {
        var metadata = new ChatClientMetadata(
            providerName: Provider,
            providerUri: null,
            defaultModelId: Model);
        return new FakeChatClient(metadata, responseText: "{}");
    }

    private static IChatClient CreateFailingChat(Exception toThrow) =>
        new FakeChatClient(new ChatClientMetadata(Provider, null, Model), toThrow: toThrow);

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string? _responseText;
        private readonly Exception? _toThrow;

        public FakeChatClient(ChatClientMetadata metadata, string? responseText = null, Exception? toThrow = null)
        {
            Metadata = metadata;
            _responseText = responseText;
            _toThrow = toThrow;
        }

        public ChatClientMetadata Metadata { get; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_toThrow is not null) throw _toThrow;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText ?? "{}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose() { }
    }

    private sealed class SimplePropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}