using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Normalization rules for the optional authorship/gallery metadata. Everything a skin
/// author (or a downloaded skin.json) can put in these fields passes through
/// <see cref="SkinMeta.Create"/>, so this is where "trim, cap, drop what we can't use" is proven.</summary>
public class SkinMetaTests
{
    private readonly ITestOutputHelper _out;

    public SkinMetaTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void None_is_empty_and_carries_no_fields()
    {
        var meta = SkinMeta.None;
        _out.WriteLine($"None: {meta}");
        Assert.True(meta.IsEmpty);
        Assert.Null(meta.Title);
        Assert.Null(meta.Author);
        Assert.Null(meta.Description);
        Assert.Null(meta.Version);
        Assert.Null(meta.SourceUrl);
        Assert.Empty(meta.Tags);
    }

    [Fact]
    public void Create_trims_and_blank_becomes_null()
    {
        var meta = SkinMeta.Create("  Neon Bar  ", "\tAda\t", "   ", "  1.2 ", null, "   ");
        _out.WriteLine($"created: title='{meta.Title}' author='{meta.Author}' desc={meta.Description ?? "<null>"} version='{meta.Version}'");
        Assert.Equal("Neon Bar", meta.Title);
        Assert.Equal("Ada", meta.Author);
        Assert.Null(meta.Description);   // whitespace-only is absent, not ""
        Assert.Equal("1.2", meta.Version);
        Assert.Null(meta.SourceUrl);
        Assert.False(meta.IsEmpty);
    }

    [Fact]
    public void Create_with_nothing_returns_the_empty_meta()
    {
        var meta = SkinMeta.Create(null, "", "  ", null, Array.Empty<string>(), null);
        _out.WriteLine($"empty create: IsEmpty={meta.IsEmpty}");
        Assert.True(meta.IsEmpty);
        Assert.Equal(SkinMeta.None, meta);
    }

    [Fact]
    public void Oversized_fields_are_truncated_to_their_caps()
    {
        var meta = SkinMeta.Create(
            new string('T', SkinMeta.MaxTitleLength + 400),
            new string('A', SkinMeta.MaxAuthorLength + 400),
            new string('D', SkinMeta.MaxDescriptionLength + 4000),
            new string('V', SkinMeta.MaxVersionLength + 40),
            null, null);
        _out.WriteLine($"lengths: title={meta.Title!.Length} author={meta.Author!.Length} desc={meta.Description!.Length} version={meta.Version!.Length}");
        Assert.Equal(SkinMeta.MaxTitleLength, meta.Title!.Length);
        Assert.Equal(SkinMeta.MaxAuthorLength, meta.Author!.Length);
        Assert.Equal(SkinMeta.MaxDescriptionLength, meta.Description!.Length);
        Assert.Equal(SkinMeta.MaxVersionLength, meta.Version!.Length);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        // 60 astral characters (2 UTF-16 code units each): a code-unit truncation at an odd
        // boundary would leave half a surrogate pair and render as a replacement character.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F3A8", 60)); // 🎨
        var meta = SkinMeta.Create(null, null, null, emoji, null, null);
        _out.WriteLine($"version: len={meta.Version!.Length} text={meta.Version}");
        Assert.DoesNotContain('\uFFFD', meta.Version!);
        foreach (var (c, i) in meta.Version!.Select((c, i) => (c, i)))
            if (char.IsHighSurrogate(c))
                Assert.True(i + 1 < meta.Version!.Length && char.IsLowSurrogate(meta.Version![i + 1]),
                    "a high surrogate must still be followed by its low surrogate");
    }

    [Fact]
    public void Control_and_bidi_characters_are_stripped()
    {
        // RLO can make "gnp.exe" render as "exe.png" — a name that lies about itself has no place
        // in a credit line shown in the app or on a gallery page.
        var meta = SkinMeta.Create("Ne\u202Eon\u0007", "A\u0000d\u200Fa", null, null, null, null);
        _out.WriteLine($"sanitized: title='{meta.Title}' author='{meta.Author}'");
        Assert.Equal("Neon", meta.Title);
        Assert.Equal("Ada", meta.Author);
    }

    [Fact]
    public void Description_keeps_newlines_but_normalizes_line_endings()
    {
        var meta = SkinMeta.Create(null, null, "one\r\ntwo\rthree\nfour\tfive", null, null, null);
        _out.WriteLine("description: " + meta.Description!.Replace("\n", "\\n"));
        Assert.Equal("one\ntwo\nthree\nfour five", meta.Description);
    }

    [Fact]
    public void Title_and_author_collapse_newlines_to_spaces()
    {
        var meta = SkinMeta.Create("two\nlines", "also\r\ntwo", null, null, null, null);
        _out.WriteLine($"title='{meta.Title}' author='{meta.Author}'");
        Assert.Equal("two lines", meta.Title);
        Assert.Equal("also two", meta.Author);
    }

    [Fact]
    public void Tags_are_trimmed_deduped_case_insensitively_and_capped()
    {
        var raw = new[] { " neon ", "NEON", "bar", "", "   ", "neon" }
            .Concat(Enumerable.Range(0, SkinMeta.MaxTags + 5).Select(i => "t" + i))
            .ToArray();
        var meta = SkinMeta.Create(null, null, null, null, raw, null);
        _out.WriteLine("tags: " + string.Join("|", meta.Tags));
        Assert.Equal(SkinMeta.MaxTags, meta.Tags.Count);
        Assert.Equal("neon", meta.Tags[0]);  // first spelling wins, trimmed
        Assert.Equal("bar", meta.Tags[1]);
        Assert.Single(meta.Tags.Where(t => t.Equals("neon", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("", meta.Tags);
    }

    [Fact]
    public void Individual_tags_are_capped_in_length()
    {
        var meta = SkinMeta.Create(null, null, null, null, new[] { new string('x', SkinMeta.MaxTagLength + 50) }, null);
        _out.WriteLine($"tag length: {meta.Tags[0].Length}");
        Assert.Equal(SkinMeta.MaxTagLength, meta.Tags[0].Length);
    }

    [Theory]
    [InlineData("https://example.com/skins/neon", true)]
    [InlineData("https://example.com", true)]
    [InlineData("HTTPS://Example.com/x", true)]
    [InlineData("http://example.com/skins/neon", false)]   // plaintext: a gallery would link it
    [InlineData("ftp://example.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/windows", false)]
    [InlineData("aorineq://install-skin?url=x", false)]
    [InlineData("https://user:pw@example.com/x", false)]   // credentials in a shared link
    [InlineData("//example.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void SourceUrl_accepts_only_credential_free_https(string url, bool expectKept)
    {
        var meta = SkinMeta.Create(null, null, null, null, null, url);
        _out.WriteLine($"'{url}' -> {meta.SourceUrl ?? "<dropped>"}");
        Assert.Equal(expectKept, meta.SourceUrl is not null);
    }

    [Fact]
    public void SourceUrl_over_the_cap_is_dropped_rather_than_truncated()
    {
        // A truncated URL is a DIFFERENT destination, not a shorter one — never keep half of it.
        var url = "https://example.com/" + new string('a', SkinMeta.MaxSourceUrlLength);
        var meta = SkinMeta.Create(null, null, null, null, null, url);
        _out.WriteLine($"len={url.Length} kept={meta.SourceUrl ?? "<dropped>"}");
        Assert.Null(meta.SourceUrl);
    }

    [Fact]
    public void SourceUrl_is_kept_verbatim_when_accepted()
    {
        const string url = "https://example.com/skins/Neon%20Bar?v=2";
        var meta = SkinMeta.Create(null, null, null, null, null, url);
        _out.WriteLine("kept: " + meta.SourceUrl);
        Assert.Equal(url, meta.SourceUrl);
    }

    [Fact]
    public void Equality_compares_tags_by_value_not_by_reference()
    {
        // Roundtrip assertions (write -> load -> compare) depend on this: the loader builds a
        // different list instance holding the same tags.
        var a = SkinMeta.Create("T", "A", null, null, new[] { "x", "y" }, null);
        var b = SkinMeta.Create("T", "A", null, null, new List<string> { "x", "y" }, null);
        var c = SkinMeta.Create("T", "A", null, null, new[] { "x", "z" }, null);
        _out.WriteLine($"a==b {a == b}; a==c {a == c}; hash equal {a.GetHashCode() == b.GetHashCode()}");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ParseTags_splits_on_commas_and_FormatTags_round_trips()
    {
        var tags = SkinMeta.ParseTags(" neon , bar,, retro ,neon ");
        _out.WriteLine("parsed: " + string.Join("|", tags));
        Assert.Equal(new[] { "neon", "bar", "retro" }, tags);

        var text = SkinMeta.FormatTags(tags);
        _out.WriteLine("formatted: " + text);
        Assert.Equal("neon, bar, retro", text);
        Assert.Equal(tags, SkinMeta.ParseTags(text));
    }

    [Fact]
    public void ParseTags_of_nothing_is_empty()
    {
        Assert.Empty(SkinMeta.ParseTags(null));
        Assert.Empty(SkinMeta.ParseTags("   ,  , "));
        Assert.Equal("", SkinMeta.FormatTags(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(null, null, "neon-bar", "neon-bar")]
    [InlineData("Neon Bar", null, "neon-bar", "Neon Bar")]
    [InlineData(null, "Ada", "neon-bar", "neon-bar — by Ada")]
    [InlineData("Neon Bar", "Ada", "neon-bar", "Neon Bar — by Ada")]
    public void DisplayLabel_credits_the_author_and_falls_back_to_the_folder(
        string? title, string? author, string folder, string expected)
    {
        var label = SkinMeta.Create(title, author, null, null, null, null).DisplayLabel(folder);
        _out.WriteLine($"title={title ?? "-"} author={author ?? "-"} -> '{label}'");
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("", "", "", "", "")]
    [InlineData("   ", "\t", "\r\n", "   ", "   ")]
    [InlineData("‮", "‏", "", "⁦", "‪")]
    [InlineData(null, null, null, null, "not-a-url")]
    public void A_field_is_either_absent_or_meaningful_never_blank(
        string? title, string? author, string? description, string? version, string? url)
    {
        // SkinWriter omits metadata keys on IsEmpty alone and then serializes the fields as they
        // are, so "no field may survive normalization as an empty or whitespace string" is the
        // invariant the byte-identical-resave guarantee rests on.
        var meta = SkinMeta.Create(title, author, description, version, new[] { "  ", "" }, url);
        _out.WriteLine($"meta={meta} IsEmpty={meta.IsEmpty}");
        foreach (var (name, value) in new[]
                 {
                     ("Title", meta.Title), ("Author", meta.Author), ("Description", meta.Description),
                     ("Version", meta.Version), ("SourceUrl", meta.SourceUrl),
                 })
            Assert.True(value is null || value.Trim().Length > 0, $"{name} survived as '{value}'");
        foreach (var tag in meta.Tags)
            Assert.True(tag.Trim().Length > 0, $"a blank tag survived as '{tag}'");

        // ...and IsEmpty means exactly "every field absent", both ways.
        bool everythingAbsent = meta.Title is null && meta.Author is null && meta.Description is null
            && meta.Version is null && meta.SourceUrl is null && meta.Tags.Count == 0;
        Assert.Equal(everythingAbsent, meta.IsEmpty);
    }

    [Fact]
    public void Create_is_the_only_way_to_build_a_SkinMeta()
    {
        // The invariant above is only worth anything if nothing can bypass Create. The record's
        // constructor is private and its properties are init-only-private, so the compiler already
        // refuses `new SkinMeta { Author = "  " }` and `meta with { Author = "  " }` outside the
        // type — this pins that so a future `public` slip is caught here rather than in a file
        // somebody's gallery is trying to read.
        var publicConstructors = typeof(SkinMeta)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        _out.WriteLine("public constructors: " + publicConstructors.Length);
        Assert.Empty(publicConstructors);

        foreach (var property in typeof(SkinMeta).GetProperties())
        {
            var setter = property.GetSetMethod(nonPublic: true);
            _out.WriteLine($"{property.Name}: setter {(setter is null ? "<none>" : setter.IsPublic ? "PUBLIC" : "non-public")}");
            Assert.True(setter is null || !setter.IsPublic, $"{property.Name} has a public setter");
        }
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        // Loading a skin and saving it back re-normalizes what was already normalized; if that
        // were not a no-op, a skin would drift a little on every round trip.
        var once = SkinMeta.Create(new string('T', 200), "Ada", "line\r\none", "1.0",
            new[] { "neon", "NEON", " bar " }, "https://example.com/x");
        var twice = SkinMeta.Create(once.Title, once.Author, once.Description, once.Version,
            once.Tags, once.SourceUrl);
        _out.WriteLine($"once : {once}\ntwice: {twice}");
        Assert.Equal(once, twice);
    }

    [Fact]
    public void DisplayLabel_is_capped_so_a_long_credit_cannot_stretch_a_picker()
    {
        var label = SkinMeta.Create(new string('T', SkinMeta.MaxTitleLength),
            new string('A', SkinMeta.MaxAuthorLength), null, null, null, null).DisplayLabel("folder");
        _out.WriteLine($"label length: {label.Length}");
        Assert.True(label.Length <= SkinMeta.MaxDisplayLabelLength,
            $"label was {label.Length} chars, cap is {SkinMeta.MaxDisplayLabelLength}");
    }
}
