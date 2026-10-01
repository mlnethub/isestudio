using System.Text.Json;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Sources.Adapters;

public sealed record S3SourceCredentials(string Region, string AccessKeyId, string SecretAccessKey, string? SessionToken);
public interface IS3SourceClientFactory { IObjectStorageSourceClient Create(S3SourceCredentials credentials); }

public sealed class S3SourceAdapter(IS3SourceClientFactory factory, ISourceSecretProtector secrets, TimeProvider clock)
    : ObjectStorageSourceAdapter(secrets, clock)
{
    public override string Kind => "s3";
    protected override Func<IObjectStorageSourceClient> Client(SourceEntity source, JsonElement config)
    {
        var credentials = new S3SourceCredentials(config.GetProperty("region").GetString()!,
            ObjectStorageSourceConfig.OpenSecret(Secrets, source, config, "access_key_id"),
            ObjectStorageSourceConfig.OpenSecret(Secrets, source, config, "secret_access_key"),
            ObjectStorageSourceConfig.OpenSecret(Secrets, source, config, "session_token", optional: true));
        return () => factory.Create(credentials);
    }
}

public sealed class S3SourceClientFactory : IS3SourceClientFactory
{
    private readonly Func<HttpMessageHandler>? _transport;
    public S3SourceClientFactory() { }
    internal S3SourceClientFactory(Func<HttpMessageHandler> transport) => _transport = transport;

    public IObjectStorageSourceClient Create(S3SourceCredentials credentials)
    {
        var suffix = credentials.Region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
        var host = $"s3.{credentials.Region}.{suffix}";
        AWSCredentials auth = string.IsNullOrEmpty(credentials.SessionToken)
            ? new BasicAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey)
            : new SessionAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken);
        var client = new OpaqueKeyS3Client(auth, new AmazonS3Config
        {
            ServiceURL = $"https://{host}", AuthenticationRegion = credentials.Region,
            ForcePathStyle = true, UseHttp = false, MaxErrorRetry = 1,
            HttpClientFactory = new FixedHttpClientFactory(host, _transport, new ObjectStorageByteBudget()),
        });
        return new S3Client(client);
    }

    private sealed class OpaqueKeyS3Client(AWSCredentials credentials, AmazonS3Config config) : AmazonS3Client(credentials, config)
    {
        protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
        {
            base.CustomizeRuntimePipeline(pipeline);
            pipeline.AddHandlerBefore<Signer>(new OpaqueKeyHandler());
        }
    }

    private sealed class OpaqueKeyHandler : PipelineHandler
    {
        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            if (executionContext.RequestContext.OriginalRequest is GetObjectRequest original && original.Key.StartsWith('/'))
            {
                var request = executionContext.RequestContext.Request;
                request.Endpoint = new Uri(request.Endpoint.GetLeftPart(UriPartial.Authority));
                request.ResourcePath = "/{Bucket}/{Key+}";
                request.PathResources["{Bucket}"] = original.BucketName;
                request.PathResources["{Key+}"] = original.Key;
            }
            return base.InvokeAsync<T>(executionContext);
        }
    }

    private sealed class FixedHttpClientFactory(string host, Func<HttpMessageHandler>? transport, ObjectStorageByteBudget budget) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
            => new(transport is null ? new ObjectStorageEndpointHandler(budget, host)
                : new ObjectStorageEndpointHandler(transport(), budget, host)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private sealed class S3Client(IAmazonS3 client) : IObjectStorageSourceClient
    {
        public async Task<ObjectStoragePage> ListAsync(string bucket, string prefix, string? token, CancellationToken ct)
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix, ContinuationToken = token, MaxKeys = 1000,
            }, ct).ConfigureAwait(false);
            if (page.IsTruncated is null || (page.IsTruncated == true && string.IsNullOrWhiteSpace(page.NextContinuationToken)))
                throw new ObjectStorageSourceException("S3 pagination is incomplete.");
            return new ObjectStoragePage((page.S3Objects ?? []).Select(item => new ObjectStorageObject(
                item.Key, item.Size ?? -1, item.ETag,
                item.LastModified is { } time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null)).ToArray(),
                page.IsTruncated == true ? page.NextContinuationToken : null);
        }

        public async Task<string?> DownloadAsync(string bucket, ObjectStorageObject item, Stream destination, CancellationToken ct)
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = item.Key, EtagToMatch = item.Version,
            }, ct).ConfigureAwait(false);
            await using var stream = response.ResponseStream;
            await stream.CopyToAsync(destination, ct).ConfigureAwait(false);
            return response.Headers.ContentType;
        }
        public void Dispose() => client.Dispose();
    }
}