using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ISEStudio.Sources;

public interface ISourceSecretProtector
{
    bool IsConfigured { get; }
    string Seal(string plaintext, string associatedData);
    string Open(string ciphertext, string associatedData);
}

public sealed class SourceSecretProtector : ISourceSecretProtector, IDisposable
{
    public const string ConfigurationKey = "ISESTUDIO_SOURCE_ENCRYPTION_KEY";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const string VersionPrefix = "v1:";

    private readonly byte[]? _key;

    public SourceSecretProtector(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _key = ParseKey(configuration[ConfigurationKey]);
    }

    public bool IsConfigured => _key is not null;

    public static void ValidateConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = ParseKey(configuration[ConfigurationKey]);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }

    public string Seal(string plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);
        var key = _key ?? throw new InvalidOperationException("Source encryption key is not configured.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var tag = new byte[TagSizeBytes];
        var cleartext = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = new byte[cleartext.Length];
        try
        {
            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Encrypt(nonce, cleartext, encrypted, tag, Encoding.UTF8.GetBytes(associatedData));
            }

            var payload = new byte[nonce.Length + tag.Length + encrypted.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, nonce.Length);
            encrypted.CopyTo(payload, nonce.Length + tag.Length);
            return VersionPrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
        }
    }

    public string Open(string ciphertext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(associatedData);
        var key = _key ?? throw new InvalidOperationException("Source encryption key is not configured.");
        if (!ciphertext.StartsWith(VersionPrefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported source secret ciphertext version.");

        var payload = Convert.FromBase64String(ciphertext[VersionPrefix.Length..]);
        if (payload.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Source secret ciphertext is invalid.");

        var nonce = payload.AsSpan(0, NonceSizeBytes);
        var tag = payload.AsSpan(NonceSizeBytes, TagSizeBytes);
        var encrypted = payload.AsSpan(NonceSizeBytes + TagSizeBytes);
        var cleartext = new byte[encrypted.Length];
        try
        {
            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Decrypt(nonce, encrypted, tag, cleartext, Encoding.UTF8.GetBytes(associatedData));
            }
            return Encoding.UTF8.GetString(cleartext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
        }
    }

    public void Dispose()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
    }

    private static byte[]? ParseKey(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be valid base64 encoding of a 32-byte key.", exception);
        }

        if (key.Length == KeySizeBytes) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException($"{ConfigurationKey} must decode to exactly 32 bytes.");
    }
}