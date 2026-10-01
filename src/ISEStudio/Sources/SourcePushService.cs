using System.Net.Http.Headers;
using System.Text.Json;
using ISEStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Primitives;

namespace ISEStudio.Sources;

public sealed class SourcePushService(SourceService sources, SourceSyncCoordinator coordinator, SourceStatementService? statements = null)
{
    public const int MaxItemBytes = 20 * 1024 * 1024;
    public const string RateLimitPolicy = "source-document-push";

    public async Task<SourceMutationResult<SourceStatementsResult>> PushStatementsAsync(Guid ksId, Guid sourceId, HttpRequest request, CancellationToken ct)
    {
        var token = ParseBearer(request.Headers.Authorization);
        if (token is null || !await sources.VerifyAsync(ksId, sourceId, token, ct, SourceKind.Statements).ConfigureAwait(false))
            return SourceMutationResult<SourceStatementsResult>.Failure(401, "Invalid Source credentials");
        if (request.ContentLength > MaxItemBytes)
            return SourceMutationResult<SourceStatementsResult>.Failure(413, "Source statements exceed 20 MiB");
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > MaxItemBytes) return SourceMutationResult<SourceStatementsResult>.Failure(413, "Source statements exceed 20 MiB");
            await content.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        SourceStatementRequest[] items;
        try
        {
            using var json = JsonDocument.Parse(content.ToArray());
            var array = json.RootElement.GetProperty("statements");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 1000)
                return SourceMutationResult<SourceStatementsResult>.Failure(400, "statements requires 1 to 1000 items");
            items = array.Deserialize<SourceStatementRequest[]>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
            if (items.Any(item => item is null)) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return SourceMutationResult<SourceStatementsResult>.Failure(400, "Invalid statements JSON");
        }
        var results = new List<SourceStatementResult>();
        foreach (var item in items)
        {
            SourceStatementResult result;
            try
            {
                result = await statements!.PushAsync(ksId, sourceId, token, item, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested
                && exception is DbUpdateException or PostgresException or InvalidOperationException)
            {
                result = new(500, null, null, 0, 0);
            }
            if (result.Status == 401) return SourceMutationResult<SourceStatementsResult>.Failure(401, "Invalid Source credentials");
            results.Add(result);
        }
        var status = results.Count == 1 ? results[0].Status : results.All(item => item.Status == 200) ? 200 : 207;
        return SourceMutationResult<SourceStatementsResult>.Success(new(results), status);
    }

    public async Task<SourceMutationResult<SourceItemResult>> PushDocumentAsync(
        Guid ksId, Guid sourceId, HttpRequest request, CancellationToken ct)
    {
        var token = ParseBearer(request.Headers.Authorization);
        if (token is null || !await sources.VerifyAsync(ksId, sourceId, token, ct, SourceKind.Api).ConfigureAwait(false))
            return SourceMutationResult<SourceItemResult>.Failure(401, "Invalid Source credentials");

        var keys = request.Query["external_key"];
        var filenames = request.Headers["X-Source-Filename"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 1024)
            return SourceMutationResult<SourceItemResult>.Failure(400, "external_key must contain 1 to 1024 characters");
        var filename = filenames.Count == 1 ? filenames[0] : null;
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 255
            || filename.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0
            || string.IsNullOrWhiteSpace(Path.GetExtension(filename)))
            return SourceMutationResult<SourceItemResult>.Failure(400, "X-Source-Filename must be a plain filename with an extension");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mime)
            || mime.MediaType is null || mime.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return SourceMutationResult<SourceItemResult>.Failure(400, "Content-Type must describe the raw document bytes");
        if (request.ContentLength > MaxItemBytes)
            return SourceMutationResult<SourceItemResult>.Failure(413, "Source document exceeds 20 MiB");

        using var content = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaxItemBytes)
                    return SourceMutationResult<SourceItemResult>.Failure(413, "Source document exceeds 20 MiB");
                await content.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return SourceMutationResult<SourceItemResult>.Failure(413, "Source document exceeds 20 MiB");
        }
        if (content.Length == 0)
            return SourceMutationResult<SourceItemResult>.Failure(400, "Source document must not be empty");
        content.Position = 0;
        try
        {
            var result = await coordinator.IngestItemAsync(sourceId,
                new SourceItem(keys[0]!, filename, mime.MediaType, content),
                async (db, source, cancellationToken) => source.KnowledgeSystemId == ksId
                    && await sources.VerifyAsync(db, ksId, sourceId, token, cancellationToken, SourceKind.Api).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            return SourceMutationResult<SourceItemResult>.Success(result);
        }
        catch (SourcePushAuthorizationException)
        {
            return SourceMutationResult<SourceItemResult>.Failure(401, "Invalid Source credentials");
        }
    }

    private static string? ParseBearer(StringValues headers)
    {
        if (headers.Count != 1 || headers[0] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..];
        if (token.Length is < 1 or > 512
            || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return null;
        return token;
    }
}

internal sealed class SourcePushAuthorizationException() : Exception("Invalid Source credentials");

/// <summary>Counts documents added or updated by one source item; unchanged retries return zero counts.</summary>
public sealed record SourceItemResult(int Added, int Updated);