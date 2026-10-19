using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The labels of a skin text's Properties - Position, Type, Outline and Shadow - in every
/// language: no two different fields may read the same, or a user cannot tell which is which.</summary>
[Collection("Loc")]
public class DesignerTypeLabelTests
{
    private readonly ITestOutputHelper _out;

    public DesignerTypeLabelTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every distinct field label a text's panel shows at once (Color and On repeat by
    /// design, one key used in several sections, so each counts once).</summary>
    private static readonly string[] TextPanelKeys =
    [
        "designer.prop.show", "designer.text.x", "designer.text.y", "designer.text.align",
        "designer.text.font", "designer.text.size", "designer.weight.label", "designer.italic.label",
        "designer.letter-spacing.label", "designer.text.color", "designer.prop.on",
        "designer.text.width", "designer.text.blur", "designer.text.depth",
    ];

    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages.All) data.Add(language);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void A_texts_fields_all_read_differently(string language)
    {
        var table = Loc.Raw(language);
        var labels = TextPanelKeys.Select(k => (Key: k, Label: table[k])).ToList();
        foreach (var (key, label) in labels) _out.WriteLine($"{language} {key} = {label}");
        var clashes = labels.GroupBy(l => l.Label).Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}': {string.Join(", ", g.Select(l => l.Key))}").ToList();
        Assert.True(clashes.Count == 0, $"{language}: fields that read the same: {string.Join("; ", clashes)}");
    }

    /// <summary>The English weight names are the OpenType names, spelled as one word each, with
    /// the number the weight is stored as.</summary>
    [Fact]
    public void The_english_weight_labels_are_the_opentype_names_with_their_numbers()
    {
        var table = Loc.Raw(Languages.En);
        var actual = new[] { "thin", "extra-light", "light", "regular", "medium", "semi-bold", "bold", "extra-bold", "black" }
            .Select(k => table["designer.weight." + k]).ToArray();
        string[] expected = ["Thin 100", "ExtraLight 200", "Light 300", "Regular 400", "Medium 500", "SemiBold 600", "Bold 700", "ExtraBold 800", "Black 900"];
        _out.WriteLine($"expected [{string.Join(", ", expected)}]");
        _out.WriteLine($"actual   [{string.Join(", ", actual)}]");
        Assert.Equal(expected, actual);
    }
}
