using System.Drawing.Imaging;

namespace AorinEQ.Core;

/// <summary>Puts the three shipped skins into the user's skins folder, rendering them from
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
/// makes this safe to call on every launch, which is how it stays true after an update adds one.</summary>
public static class DefaultSkins
{
    private static readonly string[] Layers = ["empty", "full", "airplay-empty", "airplay-full"];

    /// <summary>Writes any missing shipped skin under <paramref name="skinsRoot"/> and returns the
    /// names it created. An empty result is the normal case after the first run.</summary>
    public static IReadOnlyList<string> EnsureInstalled(string skinsRoot)
    {
        var created = new List<string>();

        foreach (var style in SkinArt.All)
        {
            var folder = Path.Combine(skinsRoot, style);
            if (Directory.Exists(folder)) continue;

            // Built beside the destination and moved into place, so a failure part-way through
            // cannot leave a half-written skin for SkinLoader to report as broken. A skin folder
            // either appears complete or does not appear.
            var staging = Path.Combine(skinsRoot, ".default-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);

                foreach (var layer in Layers)
                {
                    using var bitmap = SkinArt.Draw(style, layer);
                    bitmap.Save(Path.Combine(staging, layer + ".png"), ImageFormat.Png);
                }

                File.WriteAllText(Path.Combine(staging, "skin.json"), SkinArt.SkinJson(style));
                Directory.Move(staging, folder);
                created.Add(style);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A skins folder that cannot be written to is not a reason to fail startup: the
                // app works without any skin at all, and the Fluent OSD is still there.
                TryDelete(staging);
            }
            finally
            {
                if (Directory.Exists(staging)) TryDelete(staging);
            }
        }

        return created;
    }

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
