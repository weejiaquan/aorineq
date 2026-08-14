using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Until v3.5.1 the app had no unhandled-exception handler of any kind, so a crash was
/// completely silent: no message, no log, the tray icon simply gone. The user who hit the update
/// swap bug had to be told what happened from a Windows Error Reporting minidump. This is the
/// record that should have existed.</summary>
public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apo-crashlog-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public CrashLogTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => CrashLog.PathFor(_dir);

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex; // a real exception, with a real stack trace on it
        }
    }

    [Fact]
    public void PathFor_puts_the_log_beside_settings_json()
    {
        Assert.Equal(Path.Combine(_dir, "crash.log"), LogPath);
    }

    [Fact]
    public void Write_records_the_type_message_stack_version_and_a_timestamp()
    {
        CrashLog.Write(_dir, Thrown("the tray menu could not load an assembly"), "3.5.1+abc1234", "Dispatcher");

        var text = File.ReadAllText(LogPath);
        _out.WriteLine(text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("the tray menu could not load an assembly", text);
        Assert.Contains(nameof(Thrown), text);          // the stack trace, not just the message
        Assert.Contains("3.5.1+abc1234", text);
        Assert.Contains("Dispatcher", text);            // which handler caught it
        Assert.Contains(DateTime.Now.Year.ToString(), text);
    }

    [Fact]
    public void Write_records_inner_exceptions()
    {
        // The crash that motivated this release arrived wrapped: WinForms caught a
        // FileNotFoundException and threw from its own error dialog while reporting it.
        var inner = Thrown("Could not load file or assembly");
        var outer = new InvalidOperationException("failed while reporting a crash", inner);

        CrashLog.Write(_dir, outer, "3.5.1", "AppDomain");

        var text = File.ReadAllText(LogPath);
        _out.WriteLine(text);
        Assert.Contains("Could not load file or assembly", text);
        Assert.Contains("failed while reporting a crash", text);
    }

    [Fact]
    public void Write_appends_so_an_earlier_crash_is_still_there()
    {
        CrashLog.Write(_dir, Thrown("first crash"), "3.5.1", "Dispatcher");
        CrashLog.Write(_dir, Thrown("second crash"), "3.5.1", "Dispatcher");

        var text = File.ReadAllText(LogPath);
        _out.WriteLine(text);
        Assert.Contains("first crash", text);
        Assert.Contains("second crash", text);
        Assert.True(text.IndexOf("first crash", StringComparison.Ordinal)
            < text.IndexOf("second crash", StringComparison.Ordinal), "entries must read oldest-first");
    }

    [Fact]
    public void Write_trims_the_oldest_entries_instead_of_growing_without_bound()
    {
        // A crash loop must not fill the user's disk. The entries are deliberately fat: with
        // small ones the total never reaches the cap, and this test passed happily with the
        // trimming deleted — proving nothing. 100 x ~8 KB comfortably exceeds 256 KB.
        var padding = new string('x', 8 * 1024);
        for (var i = 0; i < 100; i++)
            CrashLog.Write(_dir, Thrown($"crash number {i} {padding}"), "3.5.1", "Dispatcher");

        var size = new FileInfo(LogPath).Length;
        var text = File.ReadAllText(LogPath);
        _out.WriteLine($"log size after 100 fat crashes: {size} bytes (cap {CrashLog.MaxBytes})");
        Assert.True(size <= CrashLog.MaxBytes, $"log grew to {size} bytes");
        Assert.Contains("crash number 99 ", text);      // the newest is what matters
        Assert.DoesNotContain("crash number 0 ", text); // the oldest were dropped
        Assert.StartsWith("=== ", text);                // and it was cut at an entry boundary
    }

    [Fact]
    public void Write_from_several_threads_at_once_keeps_every_entry()
    {
        // One bad shutdown can raise the dispatcher handler on the UI thread and the AppDomain one
        // on a background thread at the same moment. Every write is a read-modify-write of the
        // whole file, so without serialisation the entry being debugged is the one that vanishes.
        const int threads = 8, perThread = 10;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                CrashLog.Write(_dir, Thrown($"thread {t} entry {i}"), "3.5.1", "Dispatcher");
        });

        var text = File.ReadAllText(LogPath);
        var missing = new List<string>();
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                if (!text.Contains($"thread {t} entry {i}"))
                    missing.Add($"thread {t} entry {i}");

        _out.WriteLine($"{threads * perThread - missing.Count}/{threads * perThread} entries survived");
        Assert.Empty(missing);
    }

    [Fact]
    public void Write_never_throws_when_the_log_cannot_be_written()
    {
        // It runs from an unhandled-exception handler. Throwing there replaces a diagnosable
        // crash with an undiagnosable one.
        var missing = Path.Combine(_dir, "no", "such", "directory");
        CrashLog.Write(missing, Thrown("boom"), "3.5.1", "Dispatcher");

        using (File.Open(LogPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            CrashLog.Write(_dir, Thrown("boom while locked"), "3.5.1", "Dispatcher");
        }
        _out.WriteLine("both unwritable cases returned normally");
    }
}
