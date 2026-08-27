using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Every user-facing string in AorinEQ, by key, in the active language.
///
/// The tables are embedded resources - see the EmbeddedResource comment in AorinEQ.Core.csproj for
/// why they are not satellite assemblies. They are parsed once per language on first use and kept
/// for the life of the process: a table is a few hundred keys, and re-reading it on every language
/// switch would make switching feel slower than restarting the app, which is the experience this
/// whole mechanism exists to avoid.
///
/// <see cref="Language"/> is process-wide mutable state. That is the point - it is what lets a
/// language switch repaint open windows instead of demanding a restart - but it means a test that
/// sets it has to put it back (see LocScope in the test project).</summary>
public static class Loc
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Tables = new();

    private static string _language = Languages.En;

    /// <summary>Raised after <see cref="Language"/> actually changes, and not when it is set to the
    /// value it already had.
    ///
    /// Bound XAML strings repaint through LocSource, which listens to this. Strings that code
    /// assigns to a property - WinForms menu text, the tray tooltip - are re-applied by each
    /// type's own Retranslate(), which also listens here.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The active table. Always one of <see cref="Languages.All"/>: "auto" is resolved by
    /// the caller before it gets here, and anything unrecognised becomes English rather than
    /// leaving the app pointed at a table that does not exist.</summary>
    public static string Language
    {
        get => _language;
        set
        {
            var next = Languages.En;
            foreach (var language in Languages.All)
                if (language == value)
                {
                    next = value;
                    break;
                }

            if (next == _language) return;

            _language = next;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>The string for a key, in the active language.
    ///
    /// Falls back to English when the active table lacks the key, and to the key itself when no
    /// table has it. Neither is a strategy - LocKeyUsageTests fails the build on a key the app
    /// references but the table lacks, and LocCompletenessTests fails it on a key one language is
    /// missing. This is what happens if both of those are somehow wrong: a visible, searchable
    /// string on screen rather than a blank control or a crash in front of a user.</summary>
    public static string T(string key)
    {
        if (Table(_language).TryGetValue(key, out var value)) return value;
        if (Table(Languages.En).TryGetValue(key, out var english)) return english;

        return key;
    }

    /// <summary>As <see cref="T(string)"/>, with <see cref="string.Format(IFormatProvider, string, object[])"/>
    /// applied. The placeholders in every translation are pinned to the English original's by
    /// LocPlaceholderTests, because a dropped {0} loses an argument silently and an added {1}
    /// throws - in a language whoever wrote the call site cannot read.</summary>
    public static string T(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Every key in one table. Used by the tests that compare tables against each other.</summary>
    public static IReadOnlyCollection<string> Keys(string language) => Table(language).Keys;

    /// <summary>One whole table. Used by search, which reads the active language and English
    /// together, and by the tests that compare translations against their originals.</summary>
    public static IReadOnlyDictionary<string, string> Raw(string language) => Table(language);

    private static Dictionary<string, string> Table(string language) =>
        Tables.GetOrAdd(language, Load);

    private static Dictionary<string, string> Load(string language)
    {
        var resource = $"AorinEQ.Core.Strings.strings.{language}.json";

        using var stream = typeof(Loc).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"The string table '{resource}' is not embedded in AorinEQ.Core. Check the " +
                "EmbeddedResource glob in AorinEQ.Core.csproj.");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"The string table '{resource}' is empty.");

        // Keys starting "_" are file bookkeeping - which language this is, a note to translators -
        // and must never reach a control.
        var table = new Dictionary<string, string>(parsed.Count, StringComparer.Ordinal);
        foreach (var (key, value) in parsed)
            if (!key.StartsWith('_'))
                table[key] = value;

        return table;
    }
}
