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

/// <summary>Persisted app state. Autostart lives in the registry, not here.</summary>
public sealed record Settings(
    int Percent, bool Muted, bool RunAsAdmin = false,
    string OsdStyle = "dark-pill", string SkinName = "",
    string OsdAnchor = "bottom-center", int OsdOffsetX = 0, int OsdOffsetY = 0,
    double HideDelaySeconds = 1.5, bool AnimationEnabled = true, int AnimationMs = 150,
    int StepPercent = 2, string VolumeMode = VolumeModes.Eapo)
{
    public static Settings Default { get; } = new(50, false);

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
            VolumeMode = clampedMode
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
