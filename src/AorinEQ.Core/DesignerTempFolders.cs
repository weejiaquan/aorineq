using System.Diagnostics;

namespace AorinEQ.Core;

/// <summary>Where the Skin Designer keeps its temporary folders, one per use, and the startup
/// sweep of the ones a session left behind.
///
/// A designer session deletes its own folders when it closes, but not when AorinEQ is killed or
/// crashes, so the single instance sweeps both roots at startup, before a designer can exist.
/// Best effort throughout: a folder that cannot go stays for the next start, and nothing throws.</summary>
public static class DesignerTempFolders
{
    /// <summary>A session's scratch sprite sheets: <c>%TEMP%\aorineq-designer\&lt;guid&gt;</c>.</summary>
    public static string ScratchRoot => Path.Combine(Path.GetTempPath(), "aorineq-designer");

    /// <summary>Test on desktop's skin copies: <c>%TEMP%\aorineq-skin-preview\&lt;guid&gt;</c>.</summary>
    public static string PreviewRoot => Path.Combine(Path.GetTempPath(), "aorineq-skin-preview");

    /// <summary>Deletes every folder under <paramref name="root"/>. A folder with any file that
    /// cannot be opened exclusively is skipped whole: something - a viewer the user opened a sheet
    /// in - still has it, and the next start sweeps it.</summary>
    public static void DeleteStale(string root)
    {
        string[] folders;
        try
        {
            if (!Directory.Exists(root)) return;
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"DesignerTempFolders: couldn't list '{root}': {ex.Message}");
            return;
        }
        int swept = 0;
        foreach (var folder in folders)
        {
            if (HasFileInUse(folder))
            {
                Trace.TraceInformation($"DesignerTempFolders: left '{folder}': a file in it is in use.");
                continue;
            }
            try
            {
                Directory.Delete(folder, recursive: true);
                swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"DesignerTempFolders: couldn't delete '{folder}': {ex.Message}");
            }
        }
        if (swept > 0)
            Trace.TraceInformation($"DesignerTempFolders: swept {swept} stale folder(s) under '{root}'.");
    }

    /// <summary>Whether any file under <paramref name="folder"/> is held open by someone: it cannot
    /// be opened with no sharing at all. A folder that cannot even be listed counts as in use.</summary>
    private static bool HasFileInUse(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
