using System.Text;

namespace AorinEQ.Core;

/// <summary>The record a crash leaves behind, beside settings.json.
///
/// Until v3.5.1 there was none, and no unhandled-exception handler either: the app simply
/// vanished. The crash that motivated this — an update swapped in under the running process, so
/// the tray menu's first lazy assembly load threw — could only be diagnosed from a Windows Error
/// Reporting minidump, and only because one happened to be kept. A user cannot do that.
///
/// Every method here is called from an unhandled-exception handler, so nothing in it may
/// throw: a logger that fails while reporting a crash turns a diagnosable failure into an
/// undiagnosable one (which is precisely what WinForms' own ThreadExceptionDialog did on the
/// crash above).</summary>
public static class CrashLog
{
    /// <summary>Size ceiling for the whole file. A crash LOOP must not fill the disk, and the
    /// newest entry is the one worth keeping.</summary>
    public const int MaxBytes = 256 * 1024;

    public static string PathFor(string stateRoot) => Path.Combine(stateRoot, "crash.log");

    /// <summary>Appends one entry: when, which handler caught it, the running build, and the full
    /// exception including inner exceptions and stack traces. Trims the oldest entries when the
    /// file would exceed <see cref="MaxBytes"/>. Never throws.</summary>
    /// <param name="source">Which handler caught it — Dispatcher, AppDomain or TaskScheduler.
    /// Worth recording: an AppDomain entry means the process was already going down, while a
    /// Dispatcher entry means the UI thread was the one that failed.</param>
    public static void Write(string stateRoot, Exception exception, string version, string source)
    {
        try
        {
            var entry = new StringBuilder()
                .Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" | ").Append(source)
                .Append(" | AorinEQ ").Append(version)
                .AppendLine(" ===")
                .AppendLine(exception.ToString()) // type, message, stack, and every inner exception
                .AppendLine()
                .ToString();

            var path = PathFor(stateRoot);
            var existing = ReadExisting(path);
            var combined = existing + entry;
            if (Encoding.UTF8.GetByteCount(combined) > MaxBytes)
                combined = TrimToWholeEntries(combined);

            File.WriteAllText(path, combined, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            // Nothing to do and nowhere to say it. The Windows Application log still has the
            // Application Error record; this file is the friendly copy, not the only one.
        }
    }

    private static string ReadExisting(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty; // unreadable: start a fresh log rather than lose the new entry
        }
    }

    /// <summary>Drops whole entries from the front until the rest fits, so the file never opens
    /// mid-stack-trace. The newest entry is kept even if it alone exceeds the cap — a truncated
    /// newest entry is still the one being debugged.</summary>
    private static string TrimToWholeEntries(string text)
    {
        var entries = text.Split("=== ", StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        var bytes = 0;
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            var entry = "=== " + entries[i];
            var size = Encoding.UTF8.GetByteCount(entry);
            if (kept.Count > 0 && bytes + size > MaxBytes) break;
            kept.Insert(0, entry);
            bytes += size;
        }

        var result = string.Concat(kept);
        if (Encoding.UTF8.GetByteCount(result) <= MaxBytes) return result;

        // One oversized entry: keep its tail, which is where the innermost frames are.
        var chars = result.ToCharArray();
        var start = Math.Max(0, chars.Length - MaxBytes / 2);
        return "=== (earlier output trimmed) ===" + Environment.NewLine + new string(chars, start, chars.Length - start);
    }
}
