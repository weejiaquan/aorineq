using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Review focus: AorinEQ is killed mid-stream with Windows' default still on VB-Cable.
/// The original defaults were written to settings.json BEFORE the switch; the next start loads them
/// and puts each role back through <see cref="DefaultEndpointSwitcher.RestoreRecorded"/> — the very
/// method App's startup and every stream end call, not a re-enactment of it. Driven end to end with
/// a real settings file and a real default switch; the other endpoint stands in for the cable,
/// which is all the cable is to Windows here.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class AirPlayOnlyRecoveryTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public AirPlayOnlyRecoveryTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_record_left_on_disk_by_a_killed_session_puts_the_default_back_on_the_next_start()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var standIn = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(standIn);
        try
        {
            // What BeginAirPlayOnly persists (and flushes) before it switches…
            (Settings.Default with { Percent = 37, AirPlayRestoreDefault = before }).Save(SettingsPath);
            // …the switch…
            Assert.True(DefaultEndpointSwitcher.SetDefaultForAllRoles(standIn!.Id));
            _out.WriteLine($"recorded {before}; switched to {standIn.FriendlyName}; process 'dies' here");

            // …and the next start: App loads settings.json and EndAirPlayOnly runs exactly this,
            // then saves what it returns.
            var loaded = Settings.Load(SettingsPath);
            _out.WriteLine($"next start loads record: {loaded.AirPlayRestoreDefault}");
            Assert.Equal(before, loaded.AirPlayRestoreDefault);
            var (recovered, movedTo) = DefaultEndpointSwitcher.RestoreRecorded(loaded);
            recovered.Save(SettingsPath);

            var after = DefaultEndpointSwitcher.Capture();
            var reloaded = Settings.Load(SettingsPath);
            _out.WriteLine($"restore moved multimedia to {movedTo}; now {after}; record after save: "
                + (reloaded.AirPlayRestoreDefault?.ToString() ?? "(none)"));
            Assert.Equal(before, after);
            Assert.Equal(before.Multimedia, movedTo);
            Assert.Null(reloaded.AirPlayRestoreDefault);
            Assert.Equal(loaded with { AirPlayRestoreDefault = null }, recovered); // nothing else touched
            Assert.Equal(37, reloaded.Percent);
        }
        finally
        {
            DefaultRenderDevice.SetDefaultAndSettle(before.Multimedia!);
            if (before.Console is { } c) DefaultEndpointSwitcher.SetDefault(c, AudioEndpoint.ERole.Console);
            if (before.Communications is { } m) DefaultEndpointSwitcher.SetDefault(m, AudioEndpoint.ERole.Communications);
        }
    }

    /// <summary>The crash landed between saving the record and the switch (or the user put the
    /// default back themselves before relaunching): the record names the device Windows is already
    /// on. The next start must still forget it — a record left behind would keep AorinEQ believing
    /// it holds the default on the cable — while moving nothing and touching nothing else. Runs on
    /// a single-endpoint machine: nothing here needs a second device.</summary>
    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_record_of_the_current_defaults_is_forgotten_on_the_next_start_without_moving_anything()
    {
        var before = DefaultEndpointSwitcher.Capture();
        (Settings.Default with { Percent = 37, AirPlayRestoreDefault = before }).Save(SettingsPath);

        var loaded = Settings.Load(SettingsPath);
        _out.WriteLine($"next start loads record: {loaded.AirPlayRestoreDefault?.ToString() ?? "(none)"}");
        Assert.Equal(before, loaded.AirPlayRestoreDefault);
        var (recovered, movedTo) = DefaultEndpointSwitcher.RestoreRecorded(loaded);
        recovered.Save(SettingsPath);

        var after = DefaultEndpointSwitcher.Capture();
        var reloaded = Settings.Load(SettingsPath);
        _out.WriteLine($"restore moved {movedTo ?? "(nothing)"}; default {after}; record after save: "
            + (reloaded.AirPlayRestoreDefault?.ToString() ?? "(none)"));
        Assert.Null(movedTo);
        Assert.Equal(before, after);
        Assert.Null(recovered.AirPlayRestoreDefault);
        Assert.Null(reloaded.AirPlayRestoreDefault);
        Assert.Equal(loaded with { AirPlayRestoreDefault = null }, recovered); // nothing else touched
        Assert.Equal(37, reloaded.Percent);
    }

    /// <summary>The user's devices were unplugged (or uninstalled) while the app was dead: every
    /// recorded endpoint is gone. Windows' own choice stays, and the record is still cleared, so
    /// the next start does not try again forever.</summary>
    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_record_of_devices_that_are_gone_is_forgotten_and_windows_choice_stays()
    {
        const string gone = "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}";
        var before = DefaultEndpointSwitcher.Capture();
        var settings = Settings.Default with { Percent = 37, AirPlayRestoreDefault = new DefaultRenderRecord(gone, gone, gone) };

        var (after, movedTo) = DefaultEndpointSwitcher.RestoreRecorded(settings);
        _out.WriteLine($"vanished record: moved {movedTo ?? "(nothing)"}; default {DefaultEndpointSwitcher.Capture()}; "
            + $"record after: {after.AirPlayRestoreDefault?.ToString() ?? "(none)"}");
        Assert.Null(movedTo);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
        Assert.Equal(settings with { AirPlayRestoreDefault = null }, after);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_start_with_no_record_moves_nothing_and_returns_the_settings_untouched()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var settings = Settings.Default with { Percent = 37 };
        var (after, movedTo) = DefaultEndpointSwitcher.RestoreRecorded(settings);
        _out.WriteLine($"no record: moved {movedTo ?? "(nothing)"}; default {DefaultEndpointSwitcher.Capture()}");
        Assert.Same(settings, after);
        Assert.Null(movedTo);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }
}
