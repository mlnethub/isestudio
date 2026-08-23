using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OnToPilot.Storage;

/// <summary>
/// One-shot <see cref="IHostedService"/> that ensures the configured
/// MinIO bucket exists before the first <see cref="IBlobStore.PutAsync"/>
/// call. Closes the gap where a fresh MinIO instance (or an
/// accidentally-deleted bucket) would otherwise surface as
/// <c>500 AmazonS3Exception: The specified bucket does not exist</c> on
/// the first <c>POST /api/knowledge/{id}/documents/upload</c>.
/// </summary>
/// <remarks>
/// <para>This service only runs when the MinIO storage backend is
/// configured (<c>OnToPilot:Storage:Endpoint</c> is set). For the
/// <see cref="LocalCasBlobStore"/> path it is not registered at all —
/// that backend creates its blob root lazily on first write and does not
/// need a startup probe.</para>
/// <para><see cref="IHostedService.StartAsync"/> is awaited synchronously
/// by the host before the request pipeline is open, so an exception
/// here surfaces as a startup failure rather than a deferred 500. Putting
/// the create in <see cref="MinioBlobStore.EnsureBucketExistsAsync"/>
/// (which catches the already-owned race) keeps transient races from
/// failing the boot.</para>
/// </remarks>
public sealed class MinioBucketInitializer : IHostedService
{
    private readonly MinioBlobStore _store;
    private readonly ILogger<MinioBucketInitializer> _logger;

    public MinioBucketInitializer(
        IBlobStore store,
        ILogger<MinioBucketInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        // Only the MinIO-backed blob store has a bucket to ensure.
        // Program.cs registers this hosted service inside the
        // `OnToPilot:Storage:Endpoint` branch, so the cast is
        // guaranteed by DI wiring; the exception arm keeps the
        // contract explicit in tests / misuse.
        _store = store as MinioBlobStore
            ?? throw new ArgumentException(
                $"MinioBucketInitializer requires an IBlobStore backed by {nameof(MinioBlobStore)}; "
                + $"got {store.GetType().FullName} instead.",
                nameof(store));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.EnsureBucketExistsAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "MinIO bucket '{Bucket}' is ready (created or already present).",
                _store.Bucket);
        }
        catch (Exception ex)
        {
            // Surface as a startup failure rather than a deferred 500.
            // The host's HostBuilder will log this and refuse to bring
            // the application up — better than a half-broken upload path.
            _logger.LogError(ex,
                "Failed to ensure MinIO bucket '{Bucket}' exists at startup.",
                _store.Bucket);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) =>
        // Nothing to do on shutdown: the bucket is persistent state and
        // the SDK client disposes itself when the host tears down the
        // service provider.
        Task.CompletedTask;
}
