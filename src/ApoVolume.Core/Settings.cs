using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Persisted app state. Autostart lives in the registry, not here.</summary>
public sealed record Settings(int Percent, bool Muted)
{
    public static Settings Default { get; } = new(50, false);

    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            return s is null ? Default : s with { Percent = Math.Clamp(s.Percent, 0, 100) };
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
