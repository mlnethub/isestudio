using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ISEStudio.Tests.Authentication;

/// <summary>
/// Self-signed RS256 token issuer + fake discovery / JWKS docs. JwtBearer
/// pulls the openid-configuration then JWKS from the configured Authority —
/// both are served from a mock HttpMessageHandler installed by
/// <see cref="SsoTestWebApplicationFactory"/>, so the tests make no real
/// network calls.
/// </summary>
public sealed class TestJwtIssuer
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string Authority { get; } = "https://fake-keycloak.test/realms/isestudio";
    public string ClientId { get; } = "isestudio-frontend";
    public string DiscoveryPath { get; } = "/.well-known/openid-configuration";
    public string JwksPath { get; } = "/protocol/openid-connect/certs";

    public string DiscoveryJson()
    {
        return JsonSerializer.Serialize(new
        {
            issuer = Authority,
            jwks_uri = Authority + JwksPath,
        });
    }

    public string JwksJson()
    {
        var parameters = _rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "test-key",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        });
    }

    /// <summary>Issues a Keycloak-shaped access_token (iss=Authority, aud=account).</summary>
    public string CreateToken(
        string sub,
        string? azp = null,
        string? preferredUsername = null,
        string? name = null,
        string[]? realmRoles = null,
        DateTimeOffset? expiresAt = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
        };
        if (azp is not null) claims.Add(new("azp", azp));
        if (preferredUsername is not null) claims.Add(new("preferred_username", preferredUsername));
        if (name is not null) claims.Add(new("name", name));
        if (realmRoles is { Length: > 0 })
        {
            // realm_access.roles is a JSON-encoded string claim. Use the
            // literal "JSON" type marker (the System.IdentityModel.Tokens
            // constant is not pulled into the test project — keep the
            // string local so this file has no extra using).
            claims.Add(new("realm_access",
                JsonSerializer.Serialize(new { roles = realmRoles }),
                "JSON"));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = "account", // Keycloak public client aud is always "account".
            Subject = new ClaimsIdentity(claims),
            Expires = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256),
        };
        var handler = new JsonWebTokenHandler();
        handler.SetDefaultTimesOnTokenCreation = false;
        return handler.CreateToken(descriptor);
    }
}