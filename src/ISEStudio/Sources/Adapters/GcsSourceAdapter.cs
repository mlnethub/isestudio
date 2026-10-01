using System.Globalization;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources.Adapters;

public sealed record GcsSourceCredentials(string ServiceAccountKey);
public interface IGcsSourceClientFactory { IObjectStorageSourceClient Create(GcsSourceCredentials credentials); }

public sealed class GcsSourceAdapter(IGcsSourceClientFactory factory, ISourceSecretProtector secrets, TimeProvider clock)
    : ObjectStorageSourceAdapter(secrets, clock)
{
    public override string Kind => "gcs";
    protected override Func<IObjectStorageSourceClient> Client(SourceEntity source, JsonElement config)
    {
        var key = ObjectStorageSourceConfig.OpenSecret(Secrets, source, config, "service_account_key");
        if (ObjectStorageSourceConfig.ValidateServiceAccount(key) is not null)
            throw new ObjectStorageSourceException("GCS service account key is invalid.");
        return () => factory.Create(new GcsSourceCredentials(key));
    }
}

public sealed class GcsSourceClientFactory : IGcsSourceClientFactory
{
    private readonly Func<HttpMessageHandler>? _transport;
    public GcsSourceClientFactory() { }
    internal GcsSourceClientFactory(Func<HttpMessageHandler> transport) => _transport = transport;

    public IObjectStorageSourceClient Create(GcsSourceCredentials credentials)
    {
        if (ObjectStorageSourceConfig.ValidateServiceAccount(credentials.ServiceAccountKey) is not null)
            throw new ObjectStorageSourceException("GCS service account key is invalid.");
        using var json = JsonDocument.Parse(credentials.ServiceAccountKey);
        var account = json.RootElement;
        var http = new FixedHttpClientFactory(_transport, new ObjectStorageByteBudget());
        var auth = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(
            ObjectStorageSourceConfig.Text(account, "client_email"), "https://oauth2.googleapis.com/token")
        {
            ProjectId = ObjectStorageSourceConfig.Text(account, "project_id"),
            KeyId = ObjectStorageSourceConfig.Text(account, "private_key_id"),
            HttpClientFactory = http,
            Scopes = ["https://www.googleapis.com/auth/devstorage.read_only"],
        }.FromPrivateKey(ObjectStorageSourceConfig.Text(account, "private_key")));
        try
        {
            var client = new StorageClientBuilder
            {
                Credential = GoogleCredential.FromServiceAccountCredential(auth),
                BaseUri = "https://storage.googleapis.com/storage/v1/", HttpClientFactory = http,
            }.Build();
            return new GcsClient(client, auth);
        }
        catch
        {
            try { auth.HttpClient.Dispose(); }
            finally { auth.Key.Dispose(); }
            throw;
        }
    }

    private sealed class FixedHttpClientFactory(Func<HttpMessageHandler>? transport, ObjectStorageByteBudget budget) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var client = new ConfigurableHttpClient(new ConfigurableMessageHandler(
                transport is null ? new ObjectStorageEndpointHandler(budget, "storage.googleapis.com", "oauth2.googleapis.com")
                    : new ObjectStorageEndpointHandler(transport(), budget, "storage.googleapis.com", "oauth2.googleapis.com")));
            foreach (var initializer in args.Initializers) initializer.Initialize(client);
            return client;
        }
    }

    private sealed class GcsClient(StorageClient client, ServiceAccountCredential auth) : IObjectStorageSourceClient
    {
        private int _disposed;

        public async Task<ObjectStoragePage> ListAsync(string bucket, string prefix, string? token, CancellationToken ct)
        {
            var pages = client.ListObjectsAsync(bucket, prefix, new ListObjectsOptions
            {
                PageSize = 1000, PageToken = token,
                Fields = "items(name,size,generation,updated),nextPageToken",
            }).AsRawResponses();
            await using var iterator = pages.GetAsyncEnumerator(ct);
            if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                throw new ObjectStorageSourceException("GCS listing response is unavailable.");
            var page = iterator.Current;
            return new ObjectStoragePage((page.Items ?? []).Select(item => new ObjectStorageObject(item.Name,
                item.Size is { } size && size <= long.MaxValue ? (long)size : -1,
                item.Generation?.ToString(CultureInfo.InvariantCulture), item.UpdatedDateTimeOffset)).ToArray(),
                string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken);
        }

        public async Task<string?> DownloadAsync(string bucket, ObjectStorageObject item, Stream destination, CancellationToken ct)
        {
            var generation = long.Parse(item.Version!, CultureInfo.InvariantCulture);
            var result = await client.DownloadObjectAsync(bucket, item.Key, destination,
                new DownloadObjectOptions { Generation = generation, IfGenerationMatch = generation }, ct).ConfigureAwait(false);
            return result.ContentType;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { client.Service.Dispose(); }
            finally
            {
                try { auth.HttpClient.Dispose(); }
                finally { auth.Key.Dispose(); }
            }
        }
    }
}