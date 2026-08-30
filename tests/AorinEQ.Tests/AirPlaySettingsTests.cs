using System.Text.Json;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The persisted AirPlay vocabulary and the receiver volume mapping.
///
/// Everything here follows the shape the rest of Settings already uses: string constants rather
/// than enums so the JSON stays readable and an unknown value degrades instead of throwing, and
/// every field clamped in Settings.Normalize so a hand-edited file cannot produce a stream at a
/// nonsense queue depth.</summary>
public class AirPlaySettingsTests
{
    private readonly ITestOutputHelper _out;
    public AirPlaySettingsTests(ITestOutputHelper output) => _out = output;

    // ---- modes --------------------------------------------------------------------------

    [Fact]
    public void Every_mode_is_listed_and_recognised()
    {
        _out.WriteLine($"modes: {string.Join(", ", AirPlayModes.All)}");
        Assert.Equal(4, AirPlayModes.All.Count);
        foreach (var mode in AirPlayModes.All)
            Assert.True(AirPlayModes.IsMode(mode), $"{mode} is listed but not recognised");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("turbo")]
    [InlineData("REALTIME")]
    public void An_unknown_mode_falls_back_rather_than_throwing(string? value)
    {
        string normalized = AirPlayModes.Normalize(value, AirPlayModes.Normal);
        _out.WriteLine($"'{value ?? "null"}' -> '{normalized}'");
        Assert.Equal(AirPlayModes.Normal, normalized);
    }

    [Fact]
    public void Queue_depth_rises_with_each_preset()
    {
        int realtime = AirPlayModes.QueueMs(AirPlayModes.Realtime, 0);
        int normal = AirPlayModes.QueueMs(AirPlayModes.Normal, 0);
        int buffered = AirPlayModes.QueueMs(AirPlayModes.Buffered, 0);
        _out.WriteLine($"realtime={realtime}ms normal={normal}ms buffered={buffered}ms");

        Assert.True(realtime < normal, "realtime must queue less than normal");
        Assert.True(normal < buffered, "normal must queue less than buffered");
    }

    [Theory]
    [InlineData(0, AirPlayModes.MinQueueMs)]
    [InlineData(50, AirPlayModes.MinQueueMs)]
    [InlineData(750, 750)]
    [InlineData(99999, AirPlayModes.MaxQueueMs)]
    public void Custom_queue_is_clamped_to_a_sane_range(int requested, int expected)
    {
        int actual = AirPlayModes.QueueMs(AirPlayModes.Custom, requested);
        _out.WriteLine($"custom {requested}ms -> {actual}ms");
        Assert.Equal(expected, actual);
    }

    // ---- volume -------------------------------------------------------------------------

    [Fact]
    public void Volume_endpoints_map_to_the_raop_scale()
    {
        _out.WriteLine($"0% -> {AirPlayVolume.ToDb(0)} dB");
        _out.WriteLine($"50% -> {AirPlayVolume.ToDb(50)} dB");
        _out.WriteLine($"100% -> {AirPlayVolume.ToDb(100)} dB");

        Assert.Equal(AirPlayVolume.MuteDb, AirPlayVolume.ToDb(0));   // -144, RAOP's mute
        Assert.Equal(-15.0, AirPlayVolume.ToDb(50), 3);
        Assert.Equal(0.0, AirPlayVolume.ToDb(100), 3);
    }

    [Fact]
    public void Volume_round_trips_for_every_audible_percent()
    {
        for (int percent = 1; percent <= 100; percent++)
        {
            double db = AirPlayVolume.ToDb(percent);
            int back = AirPlayVolume.ToPercent(db);
            if (back != percent)
                _out.WriteLine($"MISMATCH {percent}% -> {db} dB -> {back}%");
            Assert.Equal(percent, back);
        }
        _out.WriteLine("all 100 audible steps round-tripped");
    }

    [Fact]
    public void Mute_round_trips_to_zero_percent()
    {
        _out.WriteLine($"{AirPlayVolume.MuteDb} dB -> {AirPlayVolume.ToPercent(AirPlayVolume.MuteDb)}%");
        Assert.Equal(0, AirPlayVolume.ToPercent(AirPlayVolume.MuteDb));
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void Out_of_range_percent_is_clamped_not_thrown(int percent)
    {
        double db = AirPlayVolume.ToDb(percent);
        _out.WriteLine($"{percent}% -> {db} dB");
        Assert.True(db == AirPlayVolume.MuteDb || (db >= -30 && db <= 0), $"{db} is off the RAOP scale");
    }

    // ---- settings integration -----------------------------------------------------------

    // ---- standby / dither / mute-local ---------------------------------------------------

    [Fact]
    public void Standby_means_no_idle_timeout_at_all()
    {
        var standby = AirPlaySetting.Default with { StandbyEnabled = true, IdleDisconnectSeconds = 60 };
        _out.WriteLine($"standby on, timeout field {standby.IdleDisconnectSeconds}s "
                     + $"-> effective {standby.EffectiveIdleSeconds}s");

        // Staying connected IS having no timeout; the stored number is remembered for when the
        // user turns standby back off, but must not take effect while it is on.
        Assert.Equal(0, standby.EffectiveIdleSeconds);
    }

    [Fact]
    public void Turning_standby_off_applies_the_stored_timeout()
    {
        var hangUp = AirPlaySetting.Default with { StandbyEnabled = false, IdleDisconnectSeconds = 60 };
        _out.WriteLine($"standby off -> effective {hangUp.EffectiveIdleSeconds}s");
        Assert.Equal(60, hangUp.EffectiveIdleSeconds);
    }

    [Theory]
    [InlineData(0, AirPlayIdle.MinSeconds)]
    [InlineData(5, AirPlayIdle.MinSeconds)]
    [InlineData(600, 600)]
    [InlineData(99999, AirPlayIdle.MaxSeconds)]
    public void Idle_timeout_is_clamped(int requested, int expected)
    {
        var setting = AirPlaySetting.Default with
        {
            StandbyEnabled = false,
            IdleDisconnectSeconds = requested,
        };
        _out.WriteLine($"{requested}s -> {setting.EffectiveIdleSeconds}s");
        Assert.Equal(expected, setting.EffectiveIdleSeconds);
    }

    [Fact]
    public void Defaults_favour_staying_connected_and_keeping_the_receiver_awake()
    {
        var d = AirPlaySetting.Default;
        _out.WriteLine($"standby={d.StandbyEnabled} dither={d.DitheredSilence} "
                     + $"muteLocal={d.MuteLocalWhileStreaming}");

        Assert.True(d.StandbyEnabled, "reconnecting costs a handshake plus a buffer refill");
        Assert.True(d.DitheredSilence, "digital silence lets receivers sleep and clip the next track");
        // Off by default on purpose: whether endpoint mute reaches the loopback tap is a
        // hardware lottery, and on the losing side it mutes the AirPlay stream too.
        Assert.False(d.MuteLocalWhileStreaming);
    }

    [Fact]
    public void The_new_switches_survive_a_round_trip()
    {
        var original = Settings.Default with
        {
            AirPlay = AirPlaySetting.Default with
            {
                DitheredSilence = false,
                StandbyEnabled = false,
                IdleDisconnectSeconds = 45,
                MuteLocalWhileStreaming = true,
            },
        };
        string path = Path.Combine(Path.GetTempPath(), $"aorineq-airplay-{Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            var loaded = Settings.Load(path).AirPlay!;
            _out.WriteLine(JsonSerializer.Serialize(loaded));

            Assert.False(loaded.DitheredSilence);
            Assert.False(loaded.StandbyEnabled);
            Assert.Equal(45, loaded.IdleDisconnectSeconds);
            Assert.True(loaded.MuteLocalWhileStreaming);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Airplay_is_a_settings_section()
    {
        _out.WriteLine($"sections: {string.Join(", ", SettingsSections.All)}");
        Assert.Contains(SettingsSections.AirPlay, SettingsSections.All);
        Assert.True(SettingsSections.IsSection(SettingsSections.AirPlay));
    }

    [Fact]
    public void A_settings_file_written_before_this_feature_still_loads()
    {
        // The upgrade path: no AirPlay key at all must produce defaults, not a crash.
        string path = Path.Combine(Path.GetTempPath(), $"aorineq-airplay-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Percent":42,"Muted":false,"VolumeMode":"eapo"}""");
            var loaded = Settings.Load(path);
            _out.WriteLine($"loaded: percent={loaded.Percent} airplay={JsonSerializer.Serialize(loaded.AirPlay)}");

            Assert.Equal(42, loaded.Percent);
            Assert.NotNull(loaded.AirPlay);
            Assert.False(loaded.AirPlay!.Enabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Normalize_clamps_a_hand_edited_airplay_block()
    {
        var wild = Settings.Default with
        {
            AirPlay = new AirPlaySetting(
                Enabled: true, DeviceName: "Bedroom", DeviceId: "AA@Bedroom",
                SourceEndpointId: "", Mode: "nonsense", CustomQueueMs: 99999,
                VolumePercent: 500, AutoRetargetVolume: true),
        };

        string path = Path.Combine(Path.GetTempPath(), $"aorineq-airplay-{Guid.NewGuid():N}.json");
        try
        {
            wild.Save(path);
            var loaded = Settings.Load(path);
            var airplay = loaded.AirPlay!;
            _out.WriteLine($"mode='{airplay.Mode}' queue={airplay.CustomQueueMs} volume={airplay.VolumePercent}");

            Assert.Equal(AirPlayModes.Normal, airplay.Mode);
            Assert.Equal(AirPlayModes.MaxQueueMs, airplay.CustomQueueMs);
            Assert.Equal(100, airplay.VolumePercent);
            Assert.Equal("Bedroom", airplay.DeviceName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Settings_round_trip_preserves_an_airplay_selection()
    {
        var original = Settings.Default with
        {
            AirPlay = new AirPlaySetting(true, "Bedroom", "BE4DBCD7755B@Bedroom",
                "{0.0.0.00000000}.{abc}", AirPlayModes.Buffered, 1500, 65, false),
        };

        string path = Path.Combine(Path.GetTempPath(), $"aorineq-airplay-{Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            var loaded = Settings.Load(path).AirPlay!;
            _out.WriteLine($"round-tripped: {JsonSerializer.Serialize(loaded)}");

            Assert.Equal("BE4DBCD7755B@Bedroom", loaded.DeviceId);
            Assert.Equal(AirPlayModes.Buffered, loaded.Mode);
            Assert.Equal(65, loaded.VolumePercent);
            Assert.False(loaded.AutoRetargetVolume);
            Assert.Equal("{0.0.0.00000000}.{abc}", loaded.SourceEndpointId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>CONNECTING IS SWITCHING AIRPLAY ON.
    ///
    /// The rule lives on the setting because three entry points apply it - the OSD strip's menu,
    /// the tray's menu and the settings page's Connect button - and when it was written out at
    /// only one of them the feature became unreachable: the bar is hidden while Enabled is false,
    /// so the sole control that armed it was one that setting kept off screen.</summary>
    [Fact]
    public void Connecting_switches_airplay_on_and_records_the_receiver()
    {
        var before = AirPlaySetting.Default;
        Assert.False(before.Enabled); // the state every fresh install starts in

        var after = before.Connecting("BE4DBCD7755B@Bedroom", "Bedroom");

        Assert.True(after.Enabled);
        Assert.Equal("BE4DBCD7755B@Bedroom", after.DeviceId);
        Assert.Equal("Bedroom", after.DeviceName);
    }

    /// <summary>Everything the user configured survives connecting. A connect that quietly reset
    /// the queue depth, the source or the bar mode would be a worse bug than the one this fixes.</summary>
    [Fact]
    public void Connecting_changes_nothing_else()
    {
        var before = AirPlaySetting.Default with
        {
            Mode = AirPlayModes.Custom,
            CustomQueueMs = 1750,
            SourceEndpointId = "{0.0.0.00000000}.{abc}",
            VolumePercent = 42,
            BarVisibility = AirPlayBarVisibility.Never,
            MuteLocalWhileStreaming = true,
            StandbyEnabled = false,
            IdleDisconnectSeconds = 120,
        };

        var after = before.Connecting("id", "name");

        Assert.Equal(before with { Enabled = true, DeviceId = "id", DeviceName = "name" }, after);
    }

    /// <summary>Applied on a FAILED attempt too, which is why it is a pure transformation rather
    /// than something the connect result gates: the user is more likely to retry the receiver
    /// that refused them than to want the choice cleared, and re-picking it from a bar that has
    /// meanwhile vanished is not a retry they can perform.</summary>
    [Fact]
    public void Connecting_from_a_previous_receiver_replaces_it_and_leaves_the_switch_on()
    {
        var after = AirPlaySetting.Default
            .Connecting("first@Kitchen", "Kitchen")
            .Connecting("second@Bedroom", "Bedroom");

        Assert.True(after.Enabled);
        Assert.Equal("second@Bedroom", after.DeviceId);
        Assert.Equal("Bedroom", after.DeviceName);
    }
}
