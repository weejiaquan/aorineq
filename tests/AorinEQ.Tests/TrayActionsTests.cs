using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The vocabulary a tray mouse button can be bound to. Like
/// <see cref="SettingsSections"/>, the names are a contract in three places that must agree — the
/// persisted settings.json, the Settings window's combo items, and the app's action switch — so
/// the list, its normalization and its labels live in one place rather than as literals spread
/// across the XAML and App.</summary>
public class TrayActionsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public TrayActionsTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheBindableActionsAreExactlyTheDesignedOnesInOrder()
    {
        _out.WriteLine("actions: " + string.Join(", ", TrayActions.All));

        Assert.Equal(
            new[]
            {
                TrayActions.VolumeBar, TrayActions.Settings, TrayActions.Equalizer,
                TrayActions.Mute, TrayActions.None,
            },
            TrayActions.All);
    }

    [Fact]
    public void EveryActionNameIsDistinct()
    {
        Assert.Equal(TrayActions.All.Count, TrayActions.All.Distinct().Count());
    }

    [Fact]
    public void EveryActionIsRecognised()
    {
        foreach (var action in TrayActions.All)
        {
            _out.WriteLine($"IsAction({action}) = {TrayActions.IsAction(action)}");
            Assert.True(TrayActions.IsAction(action));
        }
    }

    /// <summary>A settings.json written by a newer build — or hand-edited — must not leave a
    /// mouse button bound to something this build cannot run. The fallback is per-button (the
    /// left button opens the volume bar, the middle one mutes), so it is passed in rather than
    /// baked into the vocabulary.</summary>
    [Theory]
    [InlineData("device-picker")] // a plausible future action this build doesn't have
    [InlineData("")]
    [InlineData("VOLUME-BAR")] // persisted values are lower-case; anything else is unknown
    [InlineData(null)]
    public void UnknownActionsNormalizeToTheCallersFallback(string? stored)
    {
        _out.WriteLine($"stored='{stored}' -> {TrayActions.Normalize(stored, TrayActions.Mute)}");
        Assert.Equal(TrayActions.Mute, TrayActions.Normalize(stored, TrayActions.Mute));
        Assert.Equal(TrayActions.VolumeBar, TrayActions.Normalize(stored, TrayActions.VolumeBar));
    }

    [Fact]
    public void AKnownActionSurvivesNormalizationUnchanged()
    {
        foreach (var action in TrayActions.All)
        {
            _out.WriteLine($"Normalize({action}) = {TrayActions.Normalize(action, TrayActions.None)}");
            Assert.Equal(action, TrayActions.Normalize(action, TrayActions.None));
        }
    }

    /// <summary>"none" is a real, selectable binding — a user who wants a tray icon that does
    /// nothing on click must be able to say so, and normalization must not treat it as unset.</summary>
    [Fact]
    public void NoneIsABindableActionNotAnEmptyValue()
    {
        Assert.True(TrayActions.IsAction(TrayActions.None));
        Assert.NotEqual("", TrayActions.None);
        Assert.Equal(TrayActions.None, TrayActions.Normalize(TrayActions.None, TrayActions.VolumeBar));
    }

    /// <summary>The combo items are labelled from here, so an action added later cannot ship with
    /// its raw persisted name showing in the UI.</summary>
    [Fact]
    public void EveryActionHasAHumanLabel()
    {
        foreach (var action in TrayActions.All)
        {
            var label = TrayActions.DisplayName(action);
            _out.WriteLine($"{action} -> \"{label}\"");
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(action, label);
        }
    }
}
