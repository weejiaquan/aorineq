using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Position/visibility of the percent-text overlay drawn on top of a skin.</summary>
public sealed record SkinText(bool Show, int X, int Y);

/// <summary>Result of loading one skin folder. Always has Name/Folder/EmptyPath/FullPath populated;
/// on failure Width/Height are 0 and Error describes what went wrong.</summary>
public sealed record SkinInfo(string Name, string Folder, string EmptyPath, string FullPath,
    int Width, int Height, SkinText? Text, double Scale, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>Loads and validates skin folders (empty.png + full.png + optional skin.json).</summary>
public static class SkinLoader
{
    private const double MinScale = 0.25;
    private const double MaxScale = 4.0;
    private const double DefaultScale = 1.0;

    /// <summary>Loads and validates a single skin folder. Never throws; any failure is reported via SkinInfo.Error.</summary>
    public static SkinInfo Load(string folder)
    {
        string name = folder;
        string emptyPath = folder;
        string fullPath = folder;

        SkinInfo Bad(string error) => new(name, folder, emptyPath, fullPath, 0, 0, null, DefaultScale, error);

        try
        {
            name = new DirectoryInfo(folder).Name;
            emptyPath = Path.Combine(folder, "empty.png");
            fullPath = Path.Combine(folder, "full.png");

            if (!Directory.Exists(folder))
                return Bad($"Skin folder not found: {folder}");
            if (!File.Exists(emptyPath))
                return Bad("empty.png not found");
            if (!File.Exists(fullPath))
                return Bad("full.png not found");

            var emptySize = PngHeader.Read(emptyPath);
            if (emptySize is null)
                return Bad("empty.png is not a valid PNG");
            var fullSize = PngHeader.Read(fullPath);
            if (fullSize is null)
                return Bad("full.png is not a valid PNG");

            if (emptySize.Value.Width != fullSize.Value.Width || emptySize.Value.Height != fullSize.Value.Height)
                return Bad(
                    $"full.png is {fullSize.Value.Width}×{fullSize.Value.Height} " +
                    $"but empty.png is {emptySize.Value.Width}×{emptySize.Value.Height}");

            SkinText? text = null;
            double scale = DefaultScale;
            string jsonPath = Path.Combine(folder, "skin.json");
            if (File.Exists(jsonPath))
            {
                SkinJson? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<SkinJson>(File.ReadAllText(jsonPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException ex)
                {
                    return Bad($"skin.json is invalid: {ex.Message}");
                }

                if (parsed?.PercentText is { } pt)
                    text = new SkinText(pt.Show, pt.X, pt.Y);
                if (parsed?.Scale is { } rawScale)
                    scale = Math.Clamp(rawScale, MinScale, MaxScale);
            }

            return new SkinInfo(name, folder, emptyPath, fullPath,
                emptySize.Value.Width, emptySize.Value.Height, text, scale, null);
        }
        catch (Exception ex)
        {
            return Bad($"Failed to load skin: {ex.Message}");
        }
    }

    /// <summary>Lists every subfolder of skinsRoot as a SkinInfo (valid and invalid alike). Empty list if root is missing.</summary>
    public static IReadOnlyList<SkinInfo> Scan(string skinsRoot)
    {
        if (!Directory.Exists(skinsRoot)) return Array.Empty<SkinInfo>();

        return Directory.GetDirectories(skinsRoot)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();
    }

    private sealed class SkinJson
    {
        public SkinTextJson? PercentText { get; set; }
        public double? Scale { get; set; }
    }

    private sealed class SkinTextJson
    {
        public bool Show { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }
}
