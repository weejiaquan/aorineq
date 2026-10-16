using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AorinEQ.Core;

/// <summary>Puts the shipped skins into the user's skins folder, rendering them from
/// <see cref="SkinArt"/> the first time they are missing.
///
/// GENERATED AT RUN TIME rather than installed. The exe is a single file and the skins folder is
/// per-user under %APPDATA%, so shipping them as files would mean either carrying an image payload
/// in the binary or teaching the installer about a per-user directory that a machine-wide install
/// cannot correctly populate for everyone. Drawing them costs a few milliseconds once.
///
/// NEVER OVERWRITES. A folder that already exists is left exactly as it is, even if it looks
/// nothing like what this would draw - somebody may have edited the shipped skin, and silently
/// restoring it on the next start is the kind of behaviour that loses an evening's work. That also
/// makes this safe to call on every launch, which is how it stays true after an update adds one.
///
/// The one addition to an existing shipped folder is a device notice (notice.png plus a "notice"
/// block in skin.json) when it has neither - skins installed before notices existed. That only ever
/// ADDS what is missing, and never to a folder whose name is not a shipped style, so it does not
/// weaken the above.</summary>
public static class DefaultSkins
{
    /// <summary>Writes any missing shipped skin under <paramref name="skinsRoot"/>, adds a
    /// missing notice to an existing one, and returns the names it created or topped up. An empty result is the normal case after the first run.</summary>
    public static IReadOnlyList<string> EnsureInstalled(string skinsRoot)
    {
        var created = new List<string>();

        foreach (var style in SkinArt.All)
        {
            var folder = Path.Combine(skinsRoot, style);
            if (Directory.Exists(folder))
            {
                if (TopUpNotice(folder, style)) created.Add(style);
                continue;
            }

            // Built beside the destination and moved into place, so a failure part-way through
            // cannot leave a half-written skin for SkinLoader to report as broken. A skin folder
            // either appears complete or does not appear.
            var staging = Path.Combine(skinsRoot, ".default-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);

                foreach (var layer in SkinArt.Layers)
                {
                    using var bitmap = SkinArt.Draw(style, layer);
                    bitmap.Save(Path.Combine(staging, layer + ".png"), ImageFormat.Png);
                }

                File.WriteAllText(Path.Combine(staging, "skin.json"), SkinArt.SkinJson(style));
                Directory.Move(staging, folder);
                created.Add(style);
            }
            catch (Exception)
            {
                // Nothing here is a reason to fail startup - a skins folder that cannot be written
                // to, or GDI+ refusing a Save (ExternalException): the app works without any skin
                // at all, and the Fluent OSD is still there. Runs on every launch, unguarded.
                TryDelete(staging);
            }
            finally
            {
                if (Directory.Exists(staging)) TryDelete(staging);
            }
        }

        return created;
    }

    /// <summary>Adds the notice art and its skin.json block to a shipped skin that predates them.
    /// Does nothing if the folder has any notice.* file or the JSON already has a "notice" key (any
    /// case, as the loader reads it - including "notice": false, which is how the Skin Designer
    /// records a notice the user turned off), and leaves every other property of the JSON as it
    /// was. Best effort: ANY failure (unreadable or duplicate-keyed JSON, a GDI+ save error, a
    /// read-only folder) skips the skin - a missing notice only means the app's own plate is used,
    /// and this runs on every launch.</summary>
    private static bool TopUpNotice(string folder, string style)
    {
        try
        {
            if (Directory.GetFiles(folder, "notice.*").Length > 0) return false;

            var jsonPath = Path.Combine(folder, "skin.json");
            var root = File.Exists(jsonPath)
                ? JsonNode.Parse(File.ReadAllText(jsonPath), CaseInsensitive) as JsonObject
                : new JsonObject(CaseInsensitive);
            if (root is null || root.ContainsKey("notice")) return false;

            using (var bitmap = SkinArt.Draw(style, "notice"))
                bitmap.Save(Path.Combine(folder, "notice.png"), ImageFormat.Png);

            root["notice"] = JsonNode.Parse(SkinArt.SkinJson(style))!["notice"]!.DeepClone();

            // Written beside the original and moved over it, so a failure mid-write cannot leave
            // a truncated skin.json - which would cost the user the whole skin, not just a notice.
            var temp = Path.Combine(folder, ".skin.json." + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, jsonPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A stray dot-folder is ignored by SkinLoader.Scan by design.
        }
    }
}
