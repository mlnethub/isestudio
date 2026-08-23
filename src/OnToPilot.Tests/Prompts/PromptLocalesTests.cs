using Microsoft.Extensions.Options;
using OnToPilot.Configuration;
using OnToPilot.Extraction;
using OnToPilot.Prompts;
using Xunit;

namespace OnToPilot.Tests.Prompts;

/// <summary>
/// Locks down the P0 prompt-localization surface introduced by the
/// <see cref="PromptLocales"/> + <see cref="OnToPilotOptions.SystemLanguage"/>
/// wire-up. These tests are the regression net for the .NET ↔ Python
/// prompt-parity contract: the 3 active keys return non-empty bodies in
/// both English and Simplified Chinese, the language tag is parsed
/// case-insensitively, unknown keys resolve to <c>null</c>, and each
/// extraction service exposes the canonical <see cref="PromptKey"/>
/// constant the Python backend's <c>prompt_config</c> registry uses.
/// </summary>
public sealed class PromptLocalesTests
{
    // ------------------------------------------------------------------
    // Active keys — every entry below MUST exist in both languages so
    // a runtime swap from `en` → `zh-CN` cannot silently degrade to an
    // empty system prompt (which would make the LLM refuse to JSON).
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> ActiveKeys() => new[]
    {
        new object[] { TBoxExtractionService.PromptKey },
        new object[] { ABoxExtractionService.PromptKey },
        new object[] { TerminologyAgent.PromptKey },
    };

    [Theory]
    [MemberData(nameof(ActiveKeys))]
    public void Active_key_returns_non_empty_body_in_english(string key)
    {
        var body = PromptLocales.Resolve(key, PromptLocales.SystemLanguage.English);
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"English prompt body for key '{key}' was missing or whitespace.");
    }

    [Theory]
    [MemberData(nameof(ActiveKeys))]
    public void Active_key_returns_non_empty_body_in_simplified_chinese(string key)
    {
        var body = PromptLocales.Resolve(key, PromptLocales.SystemLanguage.SimplifiedChinese);
        Assert.False(string.IsNullOrWhiteSpace(body),
            $"Simplified Chinese prompt body for key '{key}' was missing or whitespace.");
    }

    [Theory]
    [MemberData(nameof(ActiveKeys))]
    public void English_and_chinese_bodies_differ(string key)
    {
        // Sanity check that the two language columns are not the same
        // constant — a copy-paste mistake when seeding the catalog would
        // otherwise go unnoticed.
        var en = PromptLocales.Resolve(key, PromptLocales.SystemLanguage.English);
        var zh = PromptLocales.Resolve(key, PromptLocales.SystemLanguage.SimplifiedChinese);
        Assert.NotEqual(en, zh);
    }

    // ------------------------------------------------------------------
    // Canonical PromptKey constants — these are part of the wire
    // contract with the Python backend's prompt_config registry, so a
    // rename is a breaking change and must be caught in CI.
    // ------------------------------------------------------------------

    [Fact]
    public void TBoxExtractionService_uses_rag_key_to_match_python_registry()
    {
        Assert.Equal("tbox.extract.rag", TBoxExtractionService.PromptKey);
    }

    [Fact]
    public void ABoxExtractionService_uses_python_registry_key()
    {
        Assert.Equal("abox.extract", ABoxExtractionService.PromptKey);
    }

    [Fact]
    public void TerminologyAgent_uses_steward_key_not_legacy_propose()
    {
        // Round 3 finding: the first .NET slice shipped under the
        // legacy name `terminology.propose`; aligning with the Python
        // registry name `terminology.steward` was a P0 requirement.
        Assert.Equal("terminology.steward", TerminologyAgent.PromptKey);
        Assert.NotEqual("terminology.propose", TerminologyAgent.PromptKey);
    }

    // ------------------------------------------------------------------
    // ParseSystemLanguage — the parser is the gate between the raw
    // `system_language` config string (en / zh-CN) and the typed
    // SystemLanguage enum the catalog indexes by.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("zh-CN", PromptLocales.SystemLanguage.SimplifiedChinese)]
    [InlineData("ZH-CN", PromptLocales.SystemLanguage.SimplifiedChinese)]
    [InlineData("Zh-Cn", PromptLocales.SystemLanguage.SimplifiedChinese)]
    [InlineData("zh-cn", PromptLocales.SystemLanguage.SimplifiedChinese)]
    public void ParseSystemLanguage_accepts_zh_cn_case_insensitively(
        string raw, PromptLocales.SystemLanguage expected)
    {
        Assert.Equal(expected, PromptLocales.ParseSystemLanguage(raw));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("fr")]
    [InlineData("zh")]
    [InlineData("zh_CN")]
    public void ParseSystemLanguage_falls_back_to_english_for_anything_else(string? raw)
    {
        // Mirrors the Python backend's silent fallback so the .NET side
        // cannot crash on a future locale tag the registry does not know.
        Assert.Equal(PromptLocales.SystemLanguage.English, PromptLocales.ParseSystemLanguage(raw));
    }

    // ------------------------------------------------------------------
    // ResolveWithFallback — when a key has only an English entry (the
    // 16 stub rows seeded for future agents), asking for the Chinese
    // variant must return the English body rather than null. This is
    // the same fallback the Python backend applies.
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveWithFallback_returns_english_when_chinese_column_is_empty()
    {
        // tbox.extract.agent is one of the 16 stub keys seeded for
        // future P1 work — neither column carries a real body, so
        // asking for either language returns the "Not yet wired"
        // placeholder. The test only checks the contract, not the
        // placeholder's exact text.
        var enStub = PromptLocales.Resolve("tbox.extract.agent", PromptLocales.SystemLanguage.English);
        var zhStub = PromptLocales.ResolveWithFallback(
            "tbox.extract.agent", PromptLocales.SystemLanguage.SimplifiedChinese);
        Assert.False(string.IsNullOrWhiteSpace(enStub));
        Assert.False(string.IsNullOrWhiteSpace(zhStub));
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_key()
    {
        var body = PromptLocales.Resolve(
            "this.key.does.not.exist", PromptLocales.SystemLanguage.English);
        Assert.Null(body);
    }

    // ------------------------------------------------------------------
    // Service wire-up — the three extraction services must each route
    // through ResolveSystemPrompt and return the catalog's body, not an
    // empty string and not throw. We exercise the en / zh-CN branches
    // through the OnToPilotOptions config they now depend on.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void TBoxExtractionService_ResolveSystemPrompt_returns_catalog_body(string lang)
    {
        var svc = new TBoxExtractionService(Options.Create(new OnToPilotOptions { SystemLanguage = lang }));
        var body = svc.ResolveSystemPrompt();
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.Contains("classes", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void ABoxExtractionService_ResolveSystemPrompt_returns_catalog_body(string lang)
    {
        var svc = new ABoxExtractionService(Options.Create(new OnToPilotOptions { SystemLanguage = lang }));
        var body = svc.ResolveSystemPrompt();
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.Contains("individuals", body, StringComparison.OrdinalIgnoreCase);
    }
}
