using System.Text.Json;

namespace ISEStudio.Ontology;

public sealed record SourceOut(
    Guid DocumentId, string Filename, string? Folder, bool Exists, int ChunkCount, int AxiomCount);

public sealed record ProvenanceSourceOut(
    Guid? ChunkId, Guid? DocumentId, Guid? JobId, string? Model,
    JsonDocument? PromptSnapshot, string Method, string? Actor, JsonDocument? Review);

public sealed record ProvenanceGroupOut(string AxiomKey, IReadOnlyList<ProvenanceSourceOut> Sources);
