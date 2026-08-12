using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>How volume keys change loudness: through Equalizer APO's preamp, or the real
/// Windows endpoint volume. String constants (not an enum) so the persisted json stays
/// human-readable and unknown values normalize gracefully.</summary>
public static class VolumeModes
{
    public const string Eapo = "eapo";
    public const string System = "system";
}

/// <summary>One device's persisted volume state, keyed by its full endpoint id in
/// <see cref="Settings.DeviceVolumes"/>. The legacy top-level Percent/Muted stay authoritative
/// for the ACTIVE device (and seed devices seen for the first time), so pre-v2 settings files
/// migrate losslessly.</summary>
public sealed record DeviceVolumeSetting(int Percent, bool Muted);

/// <summary>One EQ scope's persisted assignment: the active preset's name ("" = none,
/// "(custom)" when edited after load), its clipping-prevention preamp, the scope's on/off
/// bypass, and the live band chain itself (persisted directly so custom edits survive a
/// restart without requiring a saved preset file).</summary>
public sealed record EqScopeSetting(
    string PresetName = "", double PresetPreampDb = 0, bool Enabled = true,
    IReadOnlyList<EqBand>? Bands = null);

/// <summary>Persisted app state. Autostart lives in the registry, not here.</summary>
public sealed record Settings(
    int Percent, bool Muted, bool RunAsAdmin = false,
    string OsdStyle = "dark-pill", string SkinName = "",
    string OsdAnchor = "bottom-center", int OsdOffsetX = 0, int OsdOffsetY = 0,
    double HideDelaySeconds = 1.5, bool AnimationEnabled = true, int AnimationMs = 150,
    int StepPercent = 2, string VolumeMode = VolumeModes.Eapo,
    bool ProtocolLinksEnabled = true, bool AutoUpdate = true,
    Dictionary<string, DeviceVolumeSetting>? DeviceVolumes = null,
    Dictionary<string, EqScopeSetting>? DeviceEq = null,
    EqScopeSetting? GlobalEq = null,
    string EqEditorMode = EqEditorModes.Unset)
{
    public static Settings Default { get; } = new(50, false);

    private static EqScopeSetting NormalizeScope(EqScopeSetting scope) => scope with
    {
        PresetName = scope.PresetName ?? "",
        PresetPreampDb = Math.Clamp(
            double.IsFinite(scope.PresetPreampDb) ? scope.PresetPreampDb : 0,
            EqPreset.MinPreampDb, EqPreset.MaxPreampDb),
        Bands = scope.Bands?.Select(EqPreset.Clamp).ToArray(),
    };

    private static Settings Normalize(Settings s)
    {
        var validAnchors = new[] { "top-left", "top-center", "top-right", "left-center", "right-center", "bottom-left", "bottom-center", "bottom-right" };
        var validStyles = new[] { "dark-pill", "fluent", "minimal-bar", "skin" };
        var validSteps = new[] { 1, 2, 5 };
        var validModes = new[] { VolumeModes.Eapo, VolumeModes.System };

        var clampedAnchor = Array.Exists(validAnchors, x => x == s.OsdAnchor) ? s.OsdAnchor : "bottom-center";
        var clampedStyle = Array.Exists(validStyles, x => x == s.OsdStyle) ? s.OsdStyle : "dark-pill";
        var clampedStep = Array.Exists(validSteps, x => x == s.StepPercent) ? s.StepPercent : 2;
        var clampedMode = Array.Exists(validModes, x => x == s.VolumeMode) ? s.VolumeMode : VolumeModes.Eapo;

        return s with
        {
            Percent = Math.Clamp(s.Percent, 0, 100),
            HideDelaySeconds = Math.Clamp(s.HideDelaySeconds, 0.5, 5.0),
            AnimationMs = Math.Clamp(s.AnimationMs, 50, 500),
            StepPercent = clampedStep,
            OsdAnchor = clampedAnchor,
            OsdStyle = clampedStyle,
            VolumeMode = clampedMode,
            // "" is meaningful here (never chosen), so an unknown value normalizes to it rather
            // than to a fixed face — EqEditorModes.Resolve then picks from the user's own chains.
            EqEditorMode = EqEditorModes.Normalize(s.EqEditorMode),
            DeviceVolumes = s.DeviceVolumes?
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value is not null)
                .ToDictionary(kv => kv.Key,
                    kv => kv.Value with { Percent = Math.Clamp(kv.Value.Percent, 0, 100) }),
            DeviceEq = s.DeviceEq?
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => NormalizeScope(kv.Value)),
            GlobalEq = s.GlobalEq is null ? null : NormalizeScope(s.GlobalEq),
        };
    }

    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            return s is null ? Default : Normalize(s);
        }
        catch (JsonException)
        {
            return Default;
        }
        catch (IOException)
        {
            return Default;
        }
        catch (UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public void Save(string path)
    {
        // GetDirectoryName returns "" for a bare filename (write to the current directory) —
        // CreateDirectory("") would throw, so only create when there's a directory component.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
