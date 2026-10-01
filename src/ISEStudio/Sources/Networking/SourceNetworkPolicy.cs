using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ISEStudio.Sources.Networking;

public sealed class SourceNetworkException(string message) : Exception(message);

public sealed class SourceNetworkOptions
{
    public string[] AllowedCidrs { get; set; } = Array.Empty<string>();
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxResponseBytes { get; set; } = 20 * 1024 * 1024;
    public int MaxItems { get; set; } = 1000;

    internal void Validate()
    {
        if (AllowedCidrs is null || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(30)
            || MaxResponseBytes <= 0 || MaxResponseBytes > 20 * 1024 * 1024 || MaxItems <= 0 || MaxItems > 1000)
            throw new SourceNetworkException("Source network configuration is invalid.");
    }
}

public interface ISourceDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SourceDnsResolver : ISourceDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => Dns.GetHostAddressesAsync(host, ct);
}

public sealed class SourceNetworkPolicy
{
    private static readonly IPNetwork[] NonPublicV4 = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
    }.Select(IPNetwork.Parse).ToArray();
    private const string GlobalV6Registry = "https://www.iana.org/assignments/ipv6-unicast-address-assignments/ipv6-unicast-address-assignments.xhtml";
    private static readonly IPNetwork[] AllocatedGlobalV6 = new[]
    {
        "2001:200::/23", "2001:400::/23", "2001:600::/23", "2001:800::/22",
        "2001:c00::/23", "2001:e00::/23", "2001:1200::/23", "2001:1400::/22",
        "2001:1800::/23", "2001:1a00::/23", "2001:1c00::/22", "2001:2000::/19",
        "2001:4000::/23", "2001:4200::/23", "2001:4400::/23", "2001:4600::/23",
        "2001:4800::/23", "2001:4a00::/23", "2001:4c00::/23", "2001:5000::/20",
        "2001:8000::/19", "2001:a000::/20", "2001:b000::/20", "2003::/18",
        "2400::/12", "2410::/12", "2600::/12", "2610::/23", "2620::/23", "2630::/12",
        "2800::/12", "2a00::/12", "2a10::/12", "2c00::/12",
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork[] NonPublicV6 = new[]
    {
        "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20",
    }.Select(IPNetwork.Parse).ToArray();

    private readonly ISourceDnsResolver _dns;
    private readonly IPNetwork[] _allowed;

    public SourceNetworkPolicy(ISourceDnsResolver dns, IOptions<SourceNetworkOptions> options)
    {
        _dns = dns;
        options.Value.Validate();
        _allowed = options.Value.AllowedCidrs.Select(cidr => IPNetwork.TryParse(cidr, out var network)
            ? network : throw new SourceNetworkException("Source allowed CIDR configuration is invalid.")).ToArray();
    }

    public async Task<IReadOnlyList<IPAddress>> ValidateAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SourceNetworkException("Source URL is not allowed.");

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal))
            addresses = new[] { literal };
        else
        {
            try
            {
                addresses = await _dns.ResolveAsync(uri.IdnHost, ct).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                throw new SourceNetworkException("Source DNS resolution failed.");
            }
        }
        ct.ThrowIfCancellationRequested();
        if (addresses.Length == 0 || addresses.Any(address => !IsAllowed(address)))
            throw new SourceNetworkException("Source target is not allowed.");
        return Array.AsReadOnly(addresses.Distinct().ToArray());
    }

    private bool IsAllowed(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return false;
        if (_allowed.Any(network => Contains(network, address))) return true;
        if (IPAddress.IsLoopback(address)) return false;
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !NonPublicV4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 => !address.IsIPv4MappedToIPv6 && AllocatedGlobalV6.Any(network => network.Contains(address))
                && !NonPublicV6.Any(network => network.Contains(address)),
            _ => false,
        };
    }

    private static bool Contains(IPNetwork network, IPAddress address)
        => network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address);
}

public sealed class SourceDiscoveryBudget
{
    private readonly int _maximum;
    private int _count;

    public SourceDiscoveryBudget(IOptions<SourceNetworkOptions> options)
    {
        options.Value.Validate();
        _maximum = options.Value.MaxItems;
    }

    public void CountItem()
    {
        if (Interlocked.Increment(ref _count) > _maximum)
            throw new SourceNetworkException("Source discovery exceeds the item limit.");
    }
}