using System.Security.Cryptography;
using ISEStudio.Sources;
using Microsoft.Extensions.Configuration;

namespace ISEStudio.Tests.Sources;

public sealed class SourceSecretProtectorTests
{
    [Fact]
    public void Seal_uses_fresh_nonce_and_roundtrips_with_associated_data()
    {
        using var protector = CreateProtector();
        const string plaintext = "connector-password";
        const string associatedData = "knowledge-system:source:password";

        var first = protector.Seal(plaintext, associatedData);
        var second = protector.Seal(plaintext, associatedData);

        Assert.StartsWith("v1:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, protector.Open(first, associatedData));
    }

    [Fact]
    public void Open_rejects_modified_ciphertext_and_wrong_associated_data()
    {
        using var protector = CreateProtector();
        var ciphertext = protector.Seal("secret", "ks-a:source-a:token");
        var payload = Convert.FromBase64String(ciphertext["v1:".Length..]);
        payload[12] ^= 0x01;
        var tampered = "v1:" + Convert.ToBase64String(payload);

        Assert.ThrowsAny<CryptographicException>(() => protector.Open(tampered, "ks-a:source-a:token"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Open(ciphertext, "ks-b:source-a:token"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Open(ciphertext, "ks-a:source-b:token"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Open(ciphertext, "ks-a:source-a:password"));
    }

    [Fact]
    public void Missing_key_allows_construction_but_fails_closed_for_protection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        using var protector = new SourceSecretProtector(configuration);

        Assert.False(protector.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => protector.Seal("secret", "ks:source:token"));
        Assert.Throws<InvalidOperationException>(() => protector.Open("v1:anything", "ks:source:token"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Configured_key_must_decode_to_exactly_32_bytes(int keySize)
    {
        var encoded = Convert.ToBase64String(new byte[keySize]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceSecretProtector.ConfigurationKey] = encoded,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => SourceSecretProtector.ValidateConfiguration(configuration));
    }

    private static SourceSecretProtector CreateProtector()
    {
        var encodedKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceSecretProtector.ConfigurationKey] = encodedKey,
            })
            .Build();
        return new SourceSecretProtector(configuration);
    }
}