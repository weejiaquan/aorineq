using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>A translated string's placeholders must match its English original exactly.
///
/// This is the highest-value localisation test in the suite, and the one whose absence would hurt
/// most. Loc.T(key, args) runs string.Format: drop the {0} from a Korean string and the argument
/// silently vanishes, so the user reads "Updated to AorinEQ ." with no version. Add a {1} the call
/// site does not pass and it throws FormatException - in a language the person who wrote the call
/// site cannot read, on someone else's machine, in a code path nobody here ever runs.
///
/// Neither failure is visible in review: the strings look fine.</summary>
[Collection("Loc")]
public class LocPlaceholderTests
{
    /// <summary>{0}, {1:N0}, {2:F2} - the index is what has to match; the format after the colon is
    /// the translator's business (a decimal separator may legitimately differ).</summary>
    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages.All.Where(l => l != Languages.En)) data.Add(language);
        return data;
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_match_the_english_original(string language)
    {
        var english = Loc.Raw(Languages.En);
        var mismatches = new List<string>();

        foreach (var (key, translated) in Loc.Raw(language))
        {
            if (!english.TryGetValue(key, out var source)) continue;

            var expected = Indices(source);
            var actual = Indices(translated);

            if (!expected.SetEquals(actual))
            {
                mismatches.Add(
                    $"{key}: English has {{{string.Join(",", expected.Order())}}}, " +
                    $"{language} has {{{string.Join(",", actual.Order())}}}");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"Placeholder mismatches in strings.{language}.json - each of these either loses an " +
            $"argument silently or throws at runtime:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", mismatches));

        static HashSet<int> Indices(string s) =>
            Placeholder.Matches(s).Select(m => int.Parse(m.Groups["index"].Value)).ToHashSet();
    }

    /// <summary>A literal brace that is not a placeholder breaks string.Format for the whole
    /// string, so an unescaped one introduced by a translator is a runtime exception.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void No_translation_has_an_unbalanced_or_stray_brace(string language)
    {
        var broken = new List<string>();

        foreach (var (key, value) in Loc.Raw(language))
        {
            var stripped = Placeholder.Replace(value.Replace("{{", "").Replace("}}", ""), "");
            if (stripped.Contains('{') || stripped.Contains('}'))
                broken.Add($"{key}: {value}");
        }

        Assert.True(broken.Count == 0,
            $"Braces in strings.{language}.json that are neither a placeholder nor escaped:" +
            $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", broken));
    }
}
