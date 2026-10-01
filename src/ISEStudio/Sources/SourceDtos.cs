using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Sources;

public sealed record SourceUpsertRequest(
    string? Kind,
    string? Name,
    string? Icon = null,
    int? SyncIntervalMinutes = null,
    string? SyncCron = null,
    JsonElement? Config = null);

public sealed record SourceOut(
    Guid Id,
    string Kind,
    bool SupportsPushToken,
    string Name,
    string? Icon,
    int? SyncIntervalMinutes,
    string? SyncCron,
    DateTimeOffset? LastSyncedAt,
    string LastSyncStatus,
    string? LastSyncError,
    int LastSyncAdded,
    DateTimeOffset CreatedAt,
    int DocumentCount,
    int MissingDocumentCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RssFullContentSummary? RssFullContent = null);

public sealed record SourceDetailOut(
    Guid Id,
    string Kind,
    bool SupportsPushToken,
    string Name,
    string? Icon,
    int? SyncIntervalMinutes,
    string? SyncCron,
    DateTimeOffset? LastSyncedAt,
    string LastSyncStatus,
    string? LastSyncError,
    int LastSyncAdded,
    DateTimeOffset CreatedAt,
    int DocumentCount,
    int MissingDocumentCount,
    JsonElement Config,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RssFullContentSummary? RssFullContent = null);

public sealed record RssFullContentSummary(string ContentMode, int DocumentCount, int MissingDocumentCount);

public sealed record SourceTokenOut(string Token);

public sealed record SourceSyncJobOut(Guid JobId, string Status);

public sealed record SourceSyncJobDetailOut(
    Guid Id,
    string Status,
    Guid? ActiveRunId,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

public sealed record SourceSyncRunOut(
    Guid Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int AddedCount,
    int UpdatedCount,
    string? Error);

public sealed record SourceMutationResult<T>(int StatusCode, T? Value, string? Error = null)
{
    public static SourceMutationResult<T> Success(T value, int statusCode = 200)
        => new(statusCode, value);

    public static SourceMutationResult<T> Failure(int statusCode, string error)
        => new(statusCode, default, error);
}