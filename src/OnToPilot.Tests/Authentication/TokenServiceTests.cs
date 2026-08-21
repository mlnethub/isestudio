using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnToPilot.Authentication;
using OnToPilot.Infrastructure.Persistence;
using OnToPilot.Infrastructure.Persistence.Entities;
using OnToPilot.Tests.Persistence;

namespace OnToPilot.Tests.Authentication;

/// <summary>
/// Contract tests for the bearer-token primitives: <see cref="KnowledgeApiTokenService"/>
/// and <see cref="McpTokenService"/>. These verify the entropy, encoding,
/// digest, and persistence rules that bind the two services.
/// </summary>
/// <remarks>
/// <para>The tests stand up an in-memory SQLite database per test (via
/// <see cref="DbContextFactory"/>) so the EF Core model is real and the
/// <c>EnsureCreated</c> schema matches production.</para>
/// </remarks>
public sealed class TokenServiceTests
{
    // -------------------------------------------------------------------------
    // KnowledgeApiTokenService
    // -------------------------------------------------------------------------

    [Fact]
    public void KnowledgeApiToken_GeneratePlaintext_has_at_least_32_bytes_of_entropy()
    {
        var plaintext = KnowledgeApiTokenService.GeneratePlaintext("0123456789abcdef");

        // Format is `opk_<first-10-of-public-id>_<base64url-suffix>`; the
        // suffix starts after the second underscore (position 14) and must
        // decode to at least 32 bytes — the same entropy budget the Python
        // backend promises via `secrets.token_urlsafe(32)`.
        const int suffixStart = 14; // "opk_" (4) + 10 public-id chars + "_" (1) - 1 = 14
        var suffix = plaintext[suffixStart..];
        var decoded = Base64UrlDecode(suffix);
        Assert.True(decoded.Length >= 32,
            $"Expected at least 32 bytes of entropy in the suffix, got {decoded.Length}. Plaintext: {plaintext}");
    }

    [Fact]
    public void KnowledgeApiToken_GeneratePlaintext_is_base64url_with_no_padding()
    {
        var plaintext = KnowledgeApiTokenService.GeneratePlaintext("0123456789abcdef");
        const int suffixStart = 14; // "opk_" (4) + 10 public-id chars + "_" (1) - 1 = 14
        var suffix = plaintext[suffixStart..];

        // base64url: no '+' or '/' (those are base64's two alternates) and
        // no '=' padding (base64url strips it).
        Assert.DoesNotContain('+', suffix);
        Assert.DoesNotContain('/', suffix);
        Assert.DoesNotContain('=', suffix);
        foreach (var c in suffix)
        {
            var isAlnum = c is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9';
            Assert.True(isAlnum || c == '-' || c == '_',
                $"Unexpected character {c} in base64url suffix {suffix}");
        }
    }

    [Fact]
    public void KnowledgeApiToken_GeneratePlaintext_produces_unique_values()
    {
        // 256 bits of entropy → collision probability after N samples is
        // ~ N^2 / 2^65. We sample 64 and expect zero collisions.
        var seen = new HashSet<string>();
        for (var i = 0; i < 64; i++)
        {
            var plaintext = KnowledgeApiTokenService.GeneratePlaintext("0123456789abcdef");
            Assert.True(seen.Add(plaintext), $"Duplicate token on iteration {i}: {plaintext}");
        }
    }

    [Fact]
    public void KnowledgeApiToken_GeneratePlaintext_carries_the_first_10_public_id_chars()
    {
        const string publicId = "abcdef0123456789abcdef";
        var plaintext = KnowledgeApiTokenService.GeneratePlaintext(publicId);
        // Format: "opk_<publicId[0..10]>_<suffix>"
        Assert.StartsWith("opk_abcdef0123_", plaintext);
    }

    [Fact]
    public void KnowledgeApiToken_Digest_is_sha256_hex_of_utf8_input()
    {
        const string plaintext = "opk_0123456789_aabbccddeeff";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)))
            .ToLowerInvariant();
        Assert.Equal(expected, KnowledgeApiTokenService.Digest(plaintext));
        Assert.Equal(64, KnowledgeApiTokenService.Digest(plaintext).Length);
    }

    [Fact]
    public void KnowledgeApiToken_Digest_is_deterministic_for_same_input()
    {
        const string plaintext = "opk_test1234567_aabbcc";
        Assert.Equal(
            KnowledgeApiTokenService.Digest(plaintext),
            KnowledgeApiTokenService.Digest(plaintext));
    }

    [Fact]
    public void KnowledgeApiToken_Digest_changes_when_input_changes()
    {
        var a = KnowledgeApiTokenService.Digest("opk_test1234567_aaaaaa");
        var b = KnowledgeApiTokenService.Digest("opk_test1234567_bbbbbb");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task KnowledgeApiToken_CreateAsync_persists_only_the_hash_not_the_plaintext()
    {
        using var db = NewDb(out var ks);
        var service = NewService(db);

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            KnowledgeSystemId: ks.Id,
            CreatedById: null,
            Name: "test-token",
            Scopes: new[] { "ontology:read", "vocabulary:read" },
            ExpiresAt: null), CancellationToken.None);

        // The plaintext is returned to the caller exactly once.
        Assert.False(string.IsNullOrEmpty(minted.Plaintext));
        Assert.StartsWith("opk_", minted.Plaintext);

        // No column on the persisted row contains the plaintext itself.
        var row = db.KnowledgeApiTokens.Single();
        Assert.NotEqual(minted.Plaintext, row.TokenHash);
        Assert.NotEqual(minted.Plaintext, row.TokenPrefix);
        Assert.DoesNotContain(minted.Plaintext, JsonSerializer.Serialize(row));

        // The hash column is the SHA-256 of the plaintext (lowercase hex).
        Assert.Equal(KnowledgeApiTokenService.Digest(minted.Plaintext), row.TokenHash);
    }

    [Fact]
    public async Task KnowledgeApiToken_VerifyAsync_returns_row_for_matching_plaintext()
    {
        using var db = NewDb(out var ks);
        var service = NewService(db);

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "test", new[] { "ontology:read" }, null), CancellationToken.None);

        var verified = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.NotNull(verified);
        Assert.Equal(minted.Entity.Id, verified!.Token.Id);
        Assert.Equal(ks.Id, verified.KnowledgeSystem.Id);
    }

    [Fact]
    public async Task KnowledgeApiToken_VerifyAsync_returns_null_for_unknown_plaintext()
    {
        using var db = NewDb(out var ks);
        var service = NewService(db);
        await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "test", new[] { "ontology:read" }, null), CancellationToken.None);

        var verified = await service.VerifyAsync("opk_unknown0000_zzzzzzzz", CancellationToken.None);
        Assert.Null(verified);
    }

    [Fact]
    public async Task KnowledgeApiToken_VerifyAsync_returns_null_when_row_is_deleted()
    {
        using var db = NewDb(out var ks);
        var service = NewService(db);

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "test", new[] { "ontology:read" }, null), CancellationToken.None);

        // Confirm we can authenticate it once.
        var firstTry = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.NotNull(firstTry);

        // After the row is deleted, authentication fails.
        db.KnowledgeApiTokens.Remove(minted.Entity);
        await db.SaveChangesAsync();

        var secondTry = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.Null(secondTry);
    }

    [Fact]
    public async Task KnowledgeApiToken_VerifyAsync_returns_null_when_revoked()
    {
        using var db = NewDb(out var ks);
        var service = NewService(db);

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "test", new[] { "ontology:read" }, null), CancellationToken.None);
        minted.Entity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var verified = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.Null(verified);
    }

    [Fact]
    public async Task KnowledgeApiToken_VerifyAsync_returns_null_after_expiry()
    {
        using var db = NewDb(out var ks);
        var clock = new MutableClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var service = new KnowledgeApiTokenService(db, clock, NewAllocator(db));

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "test",
            new[] { "ontology:read" },
            ExpiresAt: clock.GetUtcNow().AddMinutes(5)), CancellationToken.None);

        // Before expiry: ok.
        Assert.NotNull(await service.VerifyAsync(minted.Plaintext, CancellationToken.None));

        // After expiry: rejected.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await service.VerifyAsync(minted.Plaintext, CancellationToken.None));
    }

    [Fact]
    public void KnowledgeApiToken_NormalizeScopes_drops_unknown_entries_and_keeps_order()
    {
        var normalized = KnowledgeApiTokenService.NormalizeScopes(new[]
        {
            "provenance:read",      // known, last in canonical order
            "instances:read",       // known
            "ontology:read",        // known, first in canonical order
            "totally-not-a-scope",  // unknown, dropped
            "instances:read",       // duplicate, deduped
        });
        Assert.Equal(new[] { "ontology:read", "instances:read", "provenance:read" }, normalized);
    }

    [Fact]
    public void KnowledgeApiToken_NormalizeScopes_returns_all_five_recognized_scopes()
    {
        Assert.Equal(5, KnowledgeApiTokenService.KnownScopes.Count);
        Assert.Contains("ontology:read", KnowledgeApiTokenService.KnownScopes);
        Assert.Contains("vocabulary:read", KnowledgeApiTokenService.KnownScopes);
        Assert.Contains("instances:read", KnowledgeApiTokenService.KnownScopes);
        Assert.Contains("query:read", KnowledgeApiTokenService.KnownScopes);
        Assert.Contains("provenance:read", KnowledgeApiTokenService.KnownScopes);
    }

    // -------------------------------------------------------------------------
    // McpTokenService
    // -------------------------------------------------------------------------

    [Fact]
    public void McpToken_GeneratePlaintext_has_at_least_32_bytes_of_entropy()
    {
        var plaintext = McpTokenService.GeneratePlaintext("0123456789abcdef");
        const int suffixStart = 15; // "opm_" (4) + 10 public-id chars + "_" (1) = 15
        var suffix = plaintext[suffixStart..];
        var decoded = Base64UrlDecode(suffix);
        Assert.True(decoded.Length >= 32,
            $"Expected at least 32 bytes of entropy in the MCP suffix, got {decoded.Length}.");
    }

    [Fact]
    public void McpToken_GeneratePlaintext_uses_opm_prefix()
    {
        var plaintext = McpTokenService.GeneratePlaintext("0123456789abcdef");
        Assert.StartsWith("opm_0123456789_", plaintext);
    }

    [Fact]
    public void McpToken_Digest_is_sha256_hex_of_utf8_input()
    {
        const string plaintext = "opm_0123456789_aabbcc";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)))
            .ToLowerInvariant();
        Assert.Equal(expected, McpTokenService.Digest(plaintext));
    }

    [Fact]
    public void McpToken_KnownScopes_has_three_entries()
    {
        Assert.Equal(3, McpTokenService.KnownScopes.Count);
        Assert.Contains("mcp:read", McpTokenService.KnownScopes);
        Assert.Contains("mcp:write", McpTokenService.KnownScopes);
        Assert.Contains("mcp:manage", McpTokenService.KnownScopes);
    }

    [Fact]
    public async Task McpToken_CreateAsync_persists_hash_not_plaintext_and_binds_user_and_ks()
    {
        using var db = NewDb(out var ks);
        var user = NewUser(db);
        var service = new McpTokenService(db, TimeProvider.System, NewAllocator(db));

        var minted = await service.CreateAsync(new McpTokenCreateRequest(
            KnowledgeSystemId: ks.Id,
            UserId: user.Id,
            Name: "mcp-test",
            Scopes: new[] { "mcp:read", "mcp:write" },
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        Assert.StartsWith("opm_", minted.Plaintext);
        var row = db.McpUserTokens.Single();
        Assert.NotEqual(minted.Plaintext, row.TokenHash);
        Assert.DoesNotContain(minted.Plaintext, JsonSerializer.Serialize(row));
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(ks.Id, row.KnowledgeSystemId);
    }

    [Fact]
    public async Task McpToken_VerifyAsync_returns_row_when_plaintext_matches()
    {
        using var db = NewDb(out var ks);
        var user = NewUser(db);
        var service = new McpTokenService(db, TimeProvider.System, NewAllocator(db));

        var minted = await service.CreateAsync(new McpTokenCreateRequest(
            ks.Id, user.Id, "mcp", new[] { "mcp:read" },
            DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        var verified = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.NotNull(verified);
        Assert.Equal(minted.Entity.Id, verified!.Token.Id);
        Assert.Equal(user.Id, verified.User.Id);
    }

    [Fact]
    public async Task McpToken_VerifyAsync_rejects_when_user_is_inactive()
    {
        using var db = NewDb(out var ks);
        var user = NewUser(db, active: false);
        var service = new McpTokenService(db, TimeProvider.System, NewAllocator(db));

        var minted = await service.CreateAsync(new McpTokenCreateRequest(
            ks.Id, user.Id, "mcp", new[] { "mcp:read" },
            DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        var verified = await service.VerifyAsync(minted.Plaintext, CancellationToken.None);
        Assert.Null(verified);
    }

    [Fact]
    public async Task McpToken_VerifyAsync_rejects_after_row_deletion()
    {
        using var db = NewDb(out var ks);
        var user = NewUser(db);
        var service = new McpTokenService(db, TimeProvider.System, NewAllocator(db));

        var minted = await service.CreateAsync(new McpTokenCreateRequest(
            ks.Id, user.Id, "mcp", new[] { "mcp:read" },
            DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        db.McpUserTokens.Remove(minted.Entity);
        await db.SaveChangesAsync();

        Assert.Null(await service.VerifyAsync(minted.Plaintext, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // API bearer authentication end-to-end (handler integration)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ApiBearer_authenticates_a_freshly_plaintext_token_then_rejects_after_deletion()
    {
        // Wire the bearer handler against a per-test SQLite database with a
        // freshly minted token; confirm one successful round-trip, then
        // delete the row and confirm the handler refuses the same plaintext.
        await using var app = new AuthTestWebApplicationFactory();
        _ = app.CreateDbContext();
        var db = app.CreateDbContext();
        var ks = NewKnowledgeSystem(db);
        var service = new KnowledgeApiTokenService(db, TimeProvider.System, NewAllocator(db));

        var minted = await service.CreateAsync(new KnowledgeApiTokenCreateRequest(
            ks.Id, null, "e2e",
            new[] { "ontology:read" }, null), CancellationToken.None);
        var tokenValue = minted.Plaintext;

        // First call with the freshly-plaintext token succeeds (200).
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
        var ok = await client.GetAsync($"/api/bearer/whoami/{ks.PublicId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);

        // After deleting the row, the same plaintext is rejected (401).
        db.KnowledgeApiTokens.Remove(minted.Entity);
        await db.SaveChangesAsync();

        client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
        var rejected = await client.GetAsync($"/api/bearer/whoami/{ks.PublicId}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static OnToPilotDbContext NewDb(out KnowledgeSystemEntity ks)
    {
        var db = DbContextFactory.CreateSqlite();
        ks = NewKnowledgeSystem(db);
        return db;
    }

    /// <summary>
    /// Allocator with the same DbContext the service uses — so the
    /// per-table MAX(LegacyId)+1 read sees the rows the test already
    /// inserted and the new row's LegacyId doesn't collide.
    /// </summary>
    private static LegacyIdAllocator NewAllocator(OnToPilotDbContext db) => new(db);

    private static KnowledgeSystemEntity NewKnowledgeSystem(OnToPilotDbContext db)
    {
        var ks = new KnowledgeSystemEntity
        {
            LegacyId = TestLegacyIds.Next("knowledgesystem"),
            PublicId = "kstest" + Guid.NewGuid().ToString("N")[..10],
            Name = "ks-test",
            Description = "",
            OwnerId = null,
            GraphIri = "http://ontopilot.test/ks/" + Guid.NewGuid().ToString("N"),
            BaseIri = "http://ontopilot.test/ks/" + Guid.NewGuid().ToString("N") + "/onto#",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.KnowledgeSystems.Add(ks);
        db.SaveChanges();
        return ks;
    }

    private static UserEntity NewUser(OnToPilotDbContext db, bool active = true)
    {
        var user = new UserEntity
        {
            LegacyId = TestLegacyIds.Next("users"),
            Username = "u" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "User",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("placeholder-password-123", workFactor: 4),
            IsAdmin = false,
            Active = active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static KnowledgeApiTokenService NewService(OnToPilotDbContext db) =>
        new(db, TimeProvider.System, NewAllocator(db));

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> the tests can advance by hand to
    /// exercise the expiry path deterministically.
    /// </summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}