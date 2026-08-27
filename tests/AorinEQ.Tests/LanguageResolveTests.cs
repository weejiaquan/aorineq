using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The mapping from a Windows UI culture to one of the five tables we ship.
///
/// Chinese is the reason this is a function rather than a dictionary lookup: Windows reports
/// zh-CN, zh-SG, zh-TW, zh-HK and zh-MO, and the split that matters to a reader is SCRIPT, not
/// country. Simplified is used in the PRC and Singapore; Traditional in Taiwan, Hong Kong and
/// Macau. Getting this backwards renders text that is technically Chinese and unreadable to the
/// person in front of it - a failure nobody on the development side can see.</summary>
public class LanguageResolveTests
{
    [Theory]
    [InlineData("zh-CN", Languages.ZhHans)]
    [InlineData("zh-SG", Languages.ZhHans)]
    [InlineData("zh-Hans", Languages.ZhHans)]
    [InlineData("zh-Hans-CN", Languages.ZhHans)]
    [InlineData("zh-TW", Languages.ZhHant)]
    [InlineData("zh-HK", Languages.ZhHant)]
    [InlineData("zh-MO", Languages.ZhHant)]
    [InlineData("zh-Hant", Languages.ZhHant)]
    [InlineData("zh-Hant-TW", Languages.ZhHant)]
    [InlineData("zh", Languages.ZhHans)]
    [InlineData("ja", Languages.Ja)]
    [InlineData("ja-JP", Languages.Ja)]
    [InlineData("ko", Languages.Ko)]
    [InlineData("ko-KR", Languages.Ko)]
    [InlineData("en", Languages.En)]
    [InlineData("en-GB", Languages.En)]
    [InlineData("fr-FR", Languages.En)]
    [InlineData("de", Languages.En)]
    [InlineData("", Languages.En)]
    [InlineData(null, Languages.En)]
    [InlineData("not a culture", Languages.En)]
    [InlineData("---", Languages.En)]
    public void Resolve_maps_a_culture_to_a_shipped_table(string? culture, string expected) =>
        Assert.Equal(expected, Languages.Resolve(culture!));

    /// <summary>Case is not contractual. CultureInfo.Name is conventionally "zh-Hant-TW", but a
    /// value that has been through a config file, a registry read or a lowercasing helper arrives
    /// as "zh-hant-tw", and a reader whose script silently changed because of letter case would
    /// have no idea why.</summary>
    [Theory]
    [InlineData("ZH-HANT", Languages.ZhHant)]
    [InlineData("zh-hant", Languages.ZhHant)]
    [InlineData("zh-tw", Languages.ZhHant)]
    [InlineData("JA-JP", Languages.Ja)]
    public void Resolve_ignores_case(string culture, string expected) =>
        Assert.Equal(expected, Languages.Resolve(culture));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("ja", "ja")]
    [InlineData("auto", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("klingon", "auto")]
    [InlineData("EN", "auto")]
    public void Normalize_keeps_a_known_setting_and_rejects_anything_else(string? given, string expected) =>
        Assert.Equal(expected, Languages.Normalize(given));

    [Fact]
    public void All_is_the_five_we_ship_and_does_not_contain_auto()
    {
        Assert.Equal(new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" }, Languages.All);
        Assert.DoesNotContain(Languages.Auto, Languages.All);
    }

    /// <summary>Resolve must always land on a table that exists, whatever it is handed. It runs on
    /// CultureInfo.CurrentUICulture at startup, and a return value with no table behind it would
    /// be a crash before the first window.</summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ko-KR")]
    [InlineData("qps-ploc")]
    [InlineData("x-nonsense")]
    public void Resolve_never_returns_a_language_we_do_not_ship(string culture) =>
        Assert.Contains(Languages.Resolve(culture), Languages.All);
}
