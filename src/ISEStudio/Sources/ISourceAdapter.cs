using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources;

public interface ISourceAdapter
{
    string Kind { get; }

    Task<SourceScan> DiscoverAsync(SourceEntity source, CancellationToken cancellationToken);
}

public sealed record SourceItem(
    string ExternalKey,
    string Filename,
    string? Mime,
    Stream Content,
    DateTimeOffset? DocTime = null);

public sealed record SourceScan(IAsyncEnumerable<SourceItem> Items, bool IsComplete);