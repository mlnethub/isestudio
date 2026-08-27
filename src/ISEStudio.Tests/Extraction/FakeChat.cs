using Microsoft.Extensions.AI;

namespace ISEStudio.Tests.Extraction;

/// <summary>
/// Deterministic <see cref="IChatClient"/> stand-in for the extraction tests.
/// The queue is primed with canned assistant replies before the orchestrator
/// runs; every <see cref="GetResponseAsync"/> call pops the next one (falling
/// back to an empty delta when the queue runs dry so a longer-than-expected
/// chunk list never hangs the run).
/// </summary>
/// <remarks>
/// <para>State is per-instance rather than static so xUnit can run the
/// extraction test classes in parallel without cross-test bleed. The test
/// class exposes the instance under a <c>FakeChat</c>-named member so call
/// sites read exactly like the plan's <c>FakeChat.EnqueueValidDelta()</c>.</para>
/// <para><see cref="BlockAfter"/> makes live-progress assertions
/// deterministic: the client parks after N completed calls until
/// <see cref="Release"/> runs, so a test can poll the job row for an
/// intermediate <c>processed_chunks</c> value without racing the
/// background task.</para>
/// </remarks>
public sealed class FakeChat : IChatClient
{
    /// <summary>A minimal well-formed TBox delta the production parser accepts.</summary>
    public const string ValidTBoxDelta = """
        {
          "classes": [
            {"label": "Animal", "comment": "A living creature"},
            {"label": "Dog", "comment": "A domesticated canid"},
            {"label": "Collar", "comment": "An accessory worn by an animal"}
          ],
          "object_properties": [
            {"label": "owns", "domain": "Person", "range": "Animal"},
            {"label": "trains", "domain": "Person", "range": "Animal"}
          ],
          "data_properties": [
            {"label": "weightKg", "domain": "Animal", "range": "decimal"},
            {"label": "breed", "domain": "Dog", "range": "string"}
          ],
          "subclass_of": [{"sub": "Dog", "super": "Animal"}],
          "disjoint_with": [{"a": "Dog", "b": "Collar"}],
          "equivalent_class": []
        }
        """;

    /// <summary>A minimal well-formed ABox delta referencing the seeded <c>Person</c> class.</summary>
    public const string ValidABoxDelta = """
        {
          "individuals": [{
            "label": "Alice",
            "class": "Person",
            "evidence": "Alice is a person.",
            "attributes": [{"property": "age", "value": "42"}],
            "relations": []
          }]
        }
        """;

    /// <summary>
    /// Source text the verify harness replies are grounded in: names every
    /// <see cref="ValidTBoxDelta"/> class label plus the Dog⊑Animal subclass
    /// evidence, so the TBoxVerifyService grounding checks pass when a test
    /// extracts this text. Used by tests that run the verify pipeline against
    /// the real DI graph (ExtractionRunApiTests).
    /// </summary>
    public const string VerifySourceText =
        "The Animal kingdom has many species. A Dog is an Animal. A Collar is worn by a Dog.";

    /// <summary>
    /// Boundary-critic reply that keeps every <see cref="ValidTBoxDelta"/>
    /// candidate when the source text is <see cref="VerifySourceText"/>:
    /// keep=true type decisions at 0.95 confidence with evidence spans quoted
    /// verbatim from the source.
    /// </summary>
    public const string VerifyCriticAcceptAll = """
        {
          "class_decisions": [
            {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95, "evidence": "The Animal kingdom has many species", "reason": ""},
            {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95, "evidence": "A Dog is an Animal", "reason": ""},
            {"label": "Collar", "role": "type", "keep": true, "confidence": 0.95, "evidence": "A Collar is worn by a Dog", "reason": ""}
          ],
          "subclass_decisions": [
            {"sub": "Dog", "super": "Animal", "keep": true, "confidence": 0.95, "evidence": "A Dog is an Animal"}
          ]
        }
        """;

    /// <summary>
    /// Denotation-critic reply that re-accepts every provisionally accepted
    /// class and proposes no suffix replacements. Pairs with
    /// <see cref="VerifyCriticAcceptAll"/>.
    /// </summary>
    public const string VerifyDenotationAcceptAll = """
        {
          "class_decisions": [
            {"label": "Animal", "role": "type", "keep": true, "confidence": 0.95, "evidence": "The Animal kingdom has many species", "reason": ""},
            {"label": "Dog", "role": "type", "keep": true, "confidence": 0.95, "evidence": "A Dog is an Animal", "reason": ""},
            {"label": "Collar", "role": "type", "keep": true, "confidence": 0.95, "evidence": "A Collar is worn by a Dog", "reason": ""}
          ],
          "replacement_classes": []
        }
        """;

    private readonly Queue<string> _replies = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Snapshot of the messages passed to every completed
    /// <see cref="GetResponseAsync"/> call, in call order. Lets agent-loop
    /// tests (e.g. ConflictAgent's ReAct turns) assert what the Nth turn's
    /// conversation contained — including tool results injected by the
    /// production code between turns.
    /// </summary>
    private readonly List<IReadOnlyList<ChatMessage>> _callMessages = new();

    private int _blockAfter = -1;
    private int _calls;

    /// <summary>Copies of the message lists received on each call.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> CallMessages
    {
        get
        {
            lock (_gate) return _callMessages.ToList();
        }
    }

    /// <summary>How many times <see cref="GetResponseAsync"/> has been invoked.</summary>
    public int CallCount => Volatile.Read(ref _calls);

    /// <summary>Queue one canned TBox delta reply.</summary>
    public FakeChat EnqueueValidDelta() => Enqueue(ValidTBoxDelta);

    /// <summary>Queue <paramref name="count"/> canned TBox delta replies.</summary>
    public FakeChat EnqueueValidDeltas(int count)
    {
        for (var i = 0; i < count; i++) EnqueueValidDelta();
        return this;
    }

    /// <summary>Queue one canned ABox delta reply.</summary>
    public FakeChat EnqueueValidABoxDelta() => Enqueue(ValidABoxDelta);

    /// <summary>
    /// Queue the verify replies that keep every <see cref="ValidTBoxDelta"/>
    /// candidate: one boundary-critic reply and one denotation-critic reply,
    /// both grounded in <see cref="VerifySourceText"/>. A run whose chunk
    /// text is that source and whose extractor returned
    /// <see cref="ValidTBoxDelta"/> then passes verification untouched (no
    /// adjudicator call — the critic accepts everything).
    /// </summary>
    public FakeChat EnqueueVerifyAcceptAll()
    {
        Enqueue(VerifyCriticAcceptAll);
        Enqueue(VerifyDenotationAcceptAll);
        return this;
    }

    /// <summary>
    /// Enqueue one LLM reply shaped like a terminology proposal batch. The reply
    /// is a JSON object with <c>{"proposals": [...]}</c> containing
    /// <paramref name="count"/> proposal entries the TerminologyAgent can parse
    /// into <c>TermProposalEntity</c> rows. Mirrors the
    /// <c>backend/app/ontology/terminology_agent.py:suggest()</c> envelope:
    /// each proposal entry uses Python field names
    /// (<c>action</c>, <c>preferred_label</c>, <c>target_concept_iri</c>,
    /// <c>alternate_labels</c>, <c>language</c>, <c>confidence</c>,
    /// <c>reason</c>, <c>source_chunk_ids</c>) so the agent's JSON parser
    /// matches the production Python output. Count defaults to 3.
    /// </summary>
    public FakeChat EnqueueTerminologyProposal(int count = 3)
        => EnqueueTerminologyProposal(count, sourceChunkIds: null);

    /// <summary>
    /// Overload of <see cref="EnqueueTerminologyProposal(int)"/> that lets
    /// the caller control the <c>source_chunk_ids</c> each proposal cites.
    /// TerminologyAgent filters proposals whose cited chunks are not in the
    /// loaded set, so a test that seeds real <see cref="ISEStudio.Infrastructure.Persistence.Entities.ChunkEntity"/>
    /// rows must cite their actual Guid PKs (not the 0..count-1 indices the
    /// parameterless overload uses) for the proposals to survive
    /// <c>TryBuildProposal</c>.
    /// </summary>
    public FakeChat EnqueueTerminologyProposal(int count, IReadOnlyList<Guid>? sourceChunkIds)
    {
        var entries = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) entries.Append(',');
            var sourceIds = sourceChunkIds is { Count: > 0 }
                ? string.Join(",", sourceChunkIds.Select(id => $"\"{id}\""))
                : i.ToString();
            entries.Append($$"""
                {
                  "action": "create",
                  "preferred_label": "Term {{i}}",
                  "language": "en",
                  "alternate_labels": ["alt-{{i}}"],
                  "hidden_labels": [],
                  "description": "Auto-suggested term {{i}}",
                  "broader_concept_iri": null,
                  "mapped_entity_iri": null,
                  "confidence": 0.85,
                  "reason": "extracted from chunk {{i}}",
                  "source_chunk_ids": [{{sourceIds}}]
                }
                """);
        }
        var json = $$"""
            {
              "proposals": [{{entries}}]
            }
            """;
        Enqueue(json);
        return this;
    }

    /// <summary>Queue an arbitrary raw reply body.</summary>
    public FakeChat Enqueue(string reply)
    {
        lock (_gate) _replies.Enqueue(reply);
        return this;
    }

    /// <summary>Drop every queued reply and reset the call counter / gate.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _replies.Clear();
            _callMessages.Clear();
        }
        Volatile.Write(ref _calls, 0);
        Volatile.Write(ref _blockAfter, -1);
    }

    /// <summary>
    /// Park every call after the first <paramref name="count"/> have
    /// completed, until <see cref="Release"/> is invoked.
    /// </summary>
    public void BlockAfter(int count) => Volatile.Write(ref _blockAfter, count);

    /// <summary>Let parked calls proceed.</summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        var blockAfter = Volatile.Read(ref _blockAfter);
        if (blockAfter >= 0 && call > blockAfter)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        string reply;
        lock (_gate)
        {
            _callMessages.Add(messages.ToList());
            reply = _replies.Count > 0 ? _replies.Dequeue() : "{}";
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately a no-op: the orchestrator disposes the client it got from
    /// the factory, but the fixture owns this instance across the whole test
    /// (and releasing the gate here would defeat <see cref="BlockAfter"/>).
    /// </remarks>
    public void Dispose()
    {
    }
}
