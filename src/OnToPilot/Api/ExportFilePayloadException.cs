namespace OnToPilot.Api;

/// <summary>
/// Sentinel thrown by the dispatcher when an arm must return a raw file
/// payload instead of the standard FastAPI <c>{"detail": ...}</c> envelope.
/// <see cref="FastApiErrorMiddleware"/> catches this exception BEFORE the
/// generic <see cref="Exception"/> catch so the file bytes stream directly
/// to the response with the appropriate <c>Content-Type</c> +
/// <c>Content-Disposition</c> headers — mirrors the Python
/// <c>FileResponse(path, media_type="application/n-quads")</c> shape on
/// <c>backend/app/api/releases.py:759</c> for the
/// <c>download_export_file</c> route.
///
/// <para>Used by:
/// <list type="bullet">
///   <item><c>releases.download_export_file</c> — returns N-Quads shards
///   / bundle manifest bytes for the export job.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExportFilePayloadException : Exception
{
    /// <summary>The raw file payload to write to the response body.</summary>
    public byte[] Bytes { get; }

    /// <summary>The <c>Content-Type</c> header value (e.g. <c>application/n-quads</c>).</summary>
    public string MediaType { get; }

    /// <summary>The <c>Content-Disposition: attachment; filename="..."</c> value.</summary>
    public string FileName { get; }

    public ExportFilePayloadException(byte[] bytes, string mediaType, string fileName)
        : base($"Export payload: {fileName}")
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        Bytes = bytes;
        MediaType = mediaType;
        FileName = fileName;
    }
}