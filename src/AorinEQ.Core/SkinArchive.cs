using System.IO.Compression;

namespace AorinEQ.Core;

/// <summary>Shares skins as zip files. Import extracts by WHITELISTED FILE NAME only — archive
/// entry paths are never used to build an output path, so hostile entries ("../evil", absolute
/// paths) cannot escape the destination folder by construction.</summary>
public static class SkinArchive
{
    /// <summary>The files that MAKE a skin, and the only ones an import ever writes to disk.
    /// <see cref="SkinPreview.FileName"/> is deliberately absent: see <see cref="Import"/>.</summary>
    private static readonly string[] AllowedFiles =
        { "empty.png", "empty.gif", "full.png", "full.gif", "muted.png", "muted.gif", "skin.json" };

    /// <summary>Size cap for a skin zip fetched from an aorineq:// link — generous for
    /// animated skins, tiny next to the updater's exe cap.</summary>
    public const long MaxZipBytes = 20 * 1024 * 1024;

    /// <summary>Suggested skin name for a zip: its filename without extension.</summary>
    public static string DefaultName(string zipPath) => Path.GetFileNameWithoutExtension(zipPath);

    /// <summary>Zips a valid skin folder's files (whitelist, archive root, no folder prefix), plus
    /// a freshly generated <see cref="SkinPreview.FileName"/> — the image a gallery lists the skin
    /// by. The preview is composed HERE, from the artwork being shared, and written to a temp file
    /// rather than into the skin folder: the user's skins folder holds skins, not thumbnails, and
    /// nothing on disk can pre-empt what the zip claims the skin looks like.
    ///
    /// Preview generation is BEST EFFORT. A skin whose headers are valid but whose pixels won't
    /// decode (a truncated download) still exports, just without a thumbnail — losing the listing
    /// image is a far smaller harm than refusing to let someone share their skin.
    ///
    /// The archive is BUILT BESIDE the destination and moved into place, the same
    /// no-data-loss-window shape <see cref="Import"/> uses: writing straight to
    /// <paramref name="zipPath"/> would mean deleting whatever was already there before knowing
    /// the replacement can even be created, and a failure then left the user with neither.
    ///
    /// Refuses to export a skin the loader rejects — nobody should share a broken skin.</summary>
    public static void Export(string skinFolder, string zipPath)
    {
        var info = SkinLoader.Load(skinFolder);
        if (!info.IsValid)
            throw new InvalidOperationException($"Cannot export an invalid skin: {info.Error}");

        // Two variables on purpose: the scratch file is deleted in the finally whether or not
        // generation got far enough to produce a usable one.
        var scratchPreview = Path.Combine(Path.GetTempPath(),
            "aorineq-preview-" + Guid.NewGuid().ToString("N") + ".png");
        // Beside the destination, not in %TEMP%: the move below has to be a same-volume rename.
        var scratchZip = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? ".",
            ".aorineq-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        string? previewPath = null;
        try
        {
            try
            {
                SkinPreview.Write(info, scratchPreview);
                previewPath = scratchPreview;
            }
            catch (InvalidOperationException)
            {
                previewPath = null; // undecodable artwork: ship the skin, skip the thumbnail
            }

            using (var archive = ZipFile.Open(scratchZip, ZipArchiveMode.Create))
            {
                foreach (var fileName in AllowedFiles)
                {
                    var source = Path.Combine(skinFolder, fileName);
                    if (File.Exists(source))
                        archive.CreateEntryFromFile(source, fileName);
                }
                if (previewPath is not null)
                    archive.CreateEntryFromFile(previewPath, SkinPreview.FileName);
            }
            // Only now is anything at the destination touched, and the move REPLACES it whole —
            // so the result is exactly this export, never a mix with a previous one.
            File.Move(scratchZip, zipPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to export skin: {ex.Message}", ex);
        }
        finally
        {
            foreach (var scratch in new[] { scratchPreview, scratchZip })
            {
                try { File.Delete(scratch); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Extracts a skin zip into <c>skinsRoot\name</c> and validates the result via
    /// <see cref="SkinLoader"/>. Accepts the known files at the archive root or nested exactly one
    /// folder deep (people re-zip folders); everything else in the archive is ignored. The import
    /// is STAGED: the zip extracts into a scratch folder, validates there, and only then replaces
    /// the target — a failed import never touches an existing skin, and a successful overwrite is
    /// exactly the zip's contents (no stale files from the previous skin surviving to hybridize
    /// it via the loader's png-over-gif precedence).
    ///
    /// A bundled <see cref="SkinPreview.FileName"/> is IGNORED like any other unlisted file. It is
    /// an arbitrary image chosen by whoever built the zip — nothing ties it to the artwork inside —
    /// so trusting it would let a shared skin show the user one thing and install another. The
    /// gallery's listing image is the zip's business; the installed skin's look is the artwork's.
    /// Exporting again composes a fresh one from the pixels actually installed.
    ///
    /// Returns the folder written.</summary>
    public static string Import(string zipPath, string skinsRoot, string name)
    {
        var nameError = SkinWriter.ValidateName(name);
        if (nameError is not null)
            throw new ArgumentException(nameError, nameof(name));

        var folder = Path.Combine(skinsRoot, name.Trim());
        var staging = Path.Combine(skinsRoot, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                Directory.CreateDirectory(staging);
                foreach (var entry in archive.Entries)
                {
                    // Normalize separators; skip directory entries.
                    var parts = entry.FullName.Replace('\\', '/')
                        .Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is 0 or > 2)
                        continue; // deeper nesting is not a skin layout
                    var fileName = parts[^1];
                    if (!AllowedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        continue;
                    // Output path is built from OUR whitelist name, never the entry's path.
                    entry.ExtractToFile(Path.Combine(staging, fileName.ToLowerInvariant()), overwrite: true);
                }
            }

            var info = SkinLoader.Load(staging);
            if (!info.IsValid)
                throw new InvalidOperationException($"The zip is not a valid skin: {info.Error}");

            // Replace without a data-loss window: the existing skin is RENAMED aside (nothing
            // destroyed), the staged skin moves into place, and only then is the backup deleted.
            // If the install move fails, the original is renamed back.
            string? backup = null;
            if (Directory.Exists(folder))
            {
                backup = Path.Combine(skinsRoot, ".backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(folder, backup);
            }
            try
            {
                Directory.Move(staging, folder);
            }
            catch
            {
                if (backup is not null && !Directory.Exists(folder))
                    Directory.Move(backup, folder);
                throw;
            }
            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException) { }              // leftover .backup-* is inert: Scan skips
                catch (UnauthorizedAccessException) { } // dot-folders, and the import succeeded
            }
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidOperationException($"Failed to import skin: {ex.Message}", ex);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
