using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The string tables are EMBEDDED RESOURCES - not satellite assemblies, and not JSON
/// files sitting beside the exe.
///
/// That is a publish decision, not a style one. publish.ps1 builds single-file, self-contained,
/// with compression and IncludeNativeLibrariesForSelfExtract, and this project has already lost
/// native DLLs to a temp cleaner reaping the self-extract directory underneath a running process
/// (see BundlePin, and the crash it was written for). Anything a long-lived tray session depends
/// on has to live INSIDE the assembly image, where nothing can delete it.
///
/// A test that only asserted "T returns a string" would pass just as happily with the tables loose
/// on disk, and would go on passing right up until a user's Storage Sense run. So this one asserts
/// the mechanism: the resources exist in the built assembly, under the names Loc looks for.</summary>
[Collection("Loc")]
public class LocBundleTests
{
    [Fact]
    public void Every_shipped_language_is_embedded_in_the_core_assembly()
    {
        var names = typeof(Loc).Assembly.GetManifestResourceNames();

        foreach (var language in Languages.All)
            Assert.Contains($"AorinEQ.Core.Strings.strings.{language}.json", names);
    }

    [Fact]
    public void Every_shipped_language_loads_and_is_not_empty()
    {
        foreach (var language in Languages.All)
            Assert.NotEmpty(Loc.Keys(language));
    }

    [Fact]
    public void T_returns_the_value_for_the_active_language()
    {
        using var _ = new LocScope(Languages.En);
        Assert.Equal("Volume", Loc.T("settings.nav.volume"));
    }

    [Fact]
    public void T_returns_a_different_value_when_the_language_changes()
    {
        using var _ = new LocScope(Languages.En);
        var english = Loc.T("settings.nav.volume");

        Loc.Language = Languages.Ja;
        Assert.NotEqual(english, Loc.T("settings.nav.volume"));
    }

    [Fact]
    public void T_formats_arguments()
    {
        using var _ = new LocScope(Languages.En);
        Assert.Equal("AorinEQ: 42%", Loc.T("app.tray.tooltip.level", 42));
    }

    /// <summary>A key nobody has written yet renders as the key itself rather than blanking the
    /// control or throwing.
    ///
    /// There is deliberately no Debug-only throw here. A throw would make the test suite behave
    /// differently in Debug and Release, and the situation it would catch is already a BUILD
    /// failure: LocKeyUsageTests fails on a key the app references but the table lacks, and
    /// LocCompletenessTests fails on a key one language is missing. Catching it at build time is
    /// strictly better than catching it when a user opens the window.</summary>
    [Fact]
    public void An_unknown_key_renders_as_itself_rather_than_throwing()
    {
        using var _ = new LocScope(Languages.En);
        Assert.Equal("no.such.key.anywhere", Loc.T("no.such.key.anywhere"));
    }

    /// <summary>Metadata keys are file bookkeeping and must never reach a control.</summary>
    [Fact]
    public void Underscore_prefixed_metadata_is_not_part_of_the_table()
    {
        foreach (var language in Languages.All)
            Assert.DoesNotContain(Loc.Keys(language), k => k.StartsWith('_'));
    }

    [Fact]
    public void Setting_the_language_raises_LanguageChanged_once_per_actual_change()
    {
        using var _ = new LocScope(Languages.En);

        var raised = 0;
        void Handler() => raised++;

        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Language = Languages.Ja;
            Loc.Language = Languages.Ja;   // the same value again is not a change
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }

        Assert.Equal(1, raised);
    }

    /// <summary>Loc.Language is the one place "auto" must already have been resolved. Handing it
    /// an unknown value falls back to English rather than leaving the app with a language that has
    /// no table.</summary>
    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        using var _ = new LocScope(Languages.Ja);

        Loc.Language = "klingon";
        Assert.Equal(Languages.En, Loc.Language);
    }
}

/// <summary>Loc.Language is process-wide mutable state, so a test that sets it must put it back or
/// it silently changes what every later test sees - the kind of failure that shows up as an
/// unrelated test breaking when this file is edited.
///
/// The test project already runs collections one at a time (see the xunit.runner.json note in the
/// csproj), and everything that touches Loc joins the "Loc" collection so two of them cannot
/// interleave.</summary>
public sealed class LocScope : IDisposable
{
    private readonly string _previous;

    public LocScope(string language)
    {
        _previous = Loc.Language;
        Loc.Language = language;
    }

    public void Dispose() => Loc.Language = _previous;
}

[CollectionDefinition("Loc")]
public class LocCollection;
