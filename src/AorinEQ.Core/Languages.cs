namespace AorinEQ.Core;

/// <summary>The five string tables AorinEQ ships, and how a Windows culture picks one.
///
/// Spelled once here - like <see cref="VolumeModes"/> and <see cref="SettingsSections"/> - so the
/// language a setting names can be validated rather than trusted, and so a table added later has
/// exactly one place to be declared.</summary>
public static class Languages
{
    /// <summary>"Follow Windows". Persisted in settings; never a table name.</summary>
    public const string Auto = "auto";

    public const string En = "en";
    public const string ZhHans = "zh-Hans";
    public const string ZhHant = "zh-Hant";
    public const string Ja = "ja";
    public const string Ko = "ko";

    /// <summary>Every table that exists, in the order the language picker lists them.</summary>
    public static readonly IReadOnlyList<string> All = [En, ZhHans, ZhHant, Ja, Ko];

    /// <summary>The regions that write Chinese in the traditional script. Everything else Chinese
    /// is simplified, including a bare "zh", which Windows reports for a user who chose Chinese
    /// without a region.</summary>
    private static readonly string[] TraditionalRegions = ["TW", "HK", "MO"];

    /// <summary>The table to use for a Windows UI culture.
    ///
    /// Only the script matters, never the country on its own: a Hong Kong user and a Taiwanese
    /// user read the same table, and a Singaporean and a mainland user read the other one. An
    /// explicit script subtag ("zh-Hant-TW") wins over the region, because it is the more specific
    /// statement of the same fact.
    ///
    /// Anything unrecognised - an unsupported language, a pseudo-locale, a garbled string - is
    /// English. This runs on CurrentUICulture before the first window exists, so it cannot throw
    /// and cannot return a table that is not there.</summary>
    public static string Resolve(string cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return En;

        var parts = cultureName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return En;

        var primary = parts[0].ToLowerInvariant();

        if (primary == "zh")
        {
            for (var i = 1; i < parts.Length; i++)
            {
                if (parts[i].Equals("Hant", StringComparison.OrdinalIgnoreCase)) return ZhHant;
                if (parts[i].Equals("Hans", StringComparison.OrdinalIgnoreCase)) return ZhHans;

                foreach (var region in TraditionalRegions)
                    if (parts[i].Equals(region, StringComparison.OrdinalIgnoreCase))
                        return ZhHant;
            }

            return ZhHans;
        }

        return primary switch
        {
            "ja" => Ja,
            "ko" => Ko,
            _ => En,
        };
    }

    /// <summary>A persisted setting, made safe.
    ///
    /// Anything unrecognised becomes <see cref="Auto"/> rather than throwing: settings.json is a
    /// plain file a user can edit, and a typo in it must not stop the app starting. The comparison
    /// is case-sensitive on purpose - the app writes these values itself, and silently accepting
    /// "EN" would mean two spellings of one setting could both be in the wild.</summary>
    public static string Normalize(string? setting)
    {
        if (setting == Auto) return Auto;
        if (setting is null) return Auto;

        foreach (var language in All)
            if (setting == language)
                return setting;

        return Auto;
    }
}
