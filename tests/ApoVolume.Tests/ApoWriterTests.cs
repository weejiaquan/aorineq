using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ApoWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public ApoWriterTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.txt"), "Include: peace.txt\r\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(0.0, "Preamp: 0.0 dB")]
    [InlineData(-50.0, "Preamp: -50.0 dB")]
    [InlineData(-25.2525, "Preamp: -25.3 dB")]
    [InlineData(-120.0, "Preamp: -120.0 dB")]
    public void FormatPreamp_uses_invariant_one_decimal(double db, string expected)
    {
        Assert.Equal(expected, ApoWriter.FormatPreamp(db));
    }

    [Fact]
    public void WriteVolume_writes_preamp_line_promptly()
    {
        using var w = new ApoWriter(_dir);
        w.WriteVolume(-25.2525);

        // leading edge now runs on a ThreadPool thread, not synchronously: poll briefly.
        string? content = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            if (File.Exists(w.VolumeFilePath))
            {
                content = File.ReadAllText(w.VolumeFilePath);
                if (content == "Preamp: -25.3 dB" + Environment.NewLine) break;
            }
            Thread.Sleep(10);
        }
        _out.WriteLine($"file content after {sw.ElapsedMilliseconds} ms: " + (content?.TrimEnd() ?? "<none>"));
        Assert.Equal("Preamp: -25.3 dB" + Environment.NewLine, content);
    }

    [Fact]
    public void Spammed_writes_coalesce_and_final_value_wins()
    {
        using var w = new ApoWriter(_dir);
        for (int p = 0; p <= 100; p++)
        {
            double db = p == 0 ? -120.0 : -50.0 * (100 - p) / 99.0;
            w.WriteVolume(db);
        }
        Thread.Sleep(500);
        var content = File.ReadAllText(w.VolumeFilePath);
        _out.WriteLine($"final content: {content.TrimEnd()}; writes: {w.WriteCount} of 101");
        Assert.Equal("Preamp: 0.0 dB" + Environment.NewLine, content); // p=100 → 0 dB
        Assert.True(w.WriteCount < 101, "writes must be coalesced under key spam");
    }

    [Fact]
    public void EnsureInclude_appends_once_and_is_idempotent()
    {
        using var w = new ApoWriter(_dir);
        Assert.True(w.EnsureInclude());
        Assert.False(w.EnsureInclude()); // second call: already present
        var lines = File.ReadAllLines(w.ConfigTxtPath);
        _out.WriteLine("config.txt:\n" + string.Join("\n", lines));
        Assert.Equal("Include: peace.txt", lines[0]); // Peace include untouched, ours after it
        Assert.Single(lines.Where(l => l.Trim() == ApoWriter.IncludeLine));
    }

    [Fact]
    public void EnsureInclude_creates_config_txt_if_missing()
    {
        File.Delete(Path.Combine(_dir, "config.txt"));
        using var w = new ApoWriter(_dir);
        Assert.True(w.EnsureInclude());
        Assert.Contains(ApoWriter.IncludeLine, File.ReadAllLines(w.ConfigTxtPath).Select(l => l.Trim()));
    }

    [Fact]
    public void WriteVolume_survives_locked_volume_file()
    {
        using var w = new ApoWriter(_dir);

        using (var locker = new FileStream(w.VolumeFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var ex = Record.Exception(() => w.WriteVolume(-10));
            _out.WriteLine("exception while file locked: " + (ex?.ToString() ?? "<none>"));
            Assert.Null(ex);
        }

        Thread.Sleep(60); // >50 ms pause, past the coalescer interval, file now unlocked
        w.WriteVolume(-10);
        Thread.Sleep(200); // let the coalesced/leading write land on disk

        var content = File.ReadAllText(w.VolumeFilePath);
        _out.WriteLine("content after unlock + retry: " + content.TrimEnd());
        Assert.Equal("Preamp: -10.0 dB" + Environment.NewLine, content);
    }

    [Fact]
    public void WriteVolume_raises_WriteFailing_once_after_five_consecutive_failures()
    {
        using var w = new ApoWriter(_dir);
        int firedCount = 0;
        w.WriteFailing += () => Interlocked.Increment(ref firedCount);

        // Each call is >60ms after the previous one (past the 50ms coalescer interval), so
        // every WriteVolume call here is its own leading write attempt, not coalesced away.
        using (new FileStream(w.VolumeFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (int i = 0; i < 6; i++)
            {
                w.WriteVolume(-10);
                Thread.Sleep(70);
            }
        }
        _out.WriteLine($"WriteFailing fired {firedCount} time(s) after 6 consecutive failures");
        Assert.Equal(1, firedCount);

        // A successful write resets the streak.
        Thread.Sleep(70);
        w.WriteVolume(-10);
        Thread.Sleep(70);
        var content = File.ReadAllText(w.VolumeFilePath);
        _out.WriteLine("content after recovery write: " + content.TrimEnd());
        Assert.Equal("Preamp: -10.0 dB" + Environment.NewLine, content);
        Assert.Equal(1, firedCount); // unchanged: no new failure streak yet

        // Force 5 more consecutive failures: the event must be able to fire again.
        using (new FileStream(w.VolumeFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (int i = 0; i < 5; i++)
            {
                w.WriteVolume(-20);
                Thread.Sleep(70);
            }
        }
        _out.WriteLine($"WriteFailing fired {firedCount} time(s) total after second failure burst");
        Assert.Equal(2, firedCount);
    }

    [Fact]
    public async Task EnsureInclude_is_thread_safe_under_concurrent_calls()
    {
        using var w = new ApoWriter(_dir);
        const int taskCount = 8;
        using var barrier = new Barrier(taskCount);
        var results = new bool[taskCount];

        var tasks = new Task[taskCount];
        for (int i = 0; i < taskCount; i++)
        {
            int idx = i;
            tasks[idx] = Task.Run(() =>
            {
                barrier.SignalAndWait(); // all tasks call EnsureInclude at the same instant
                results[idx] = w.EnsureInclude();
            });
        }
        await Task.WhenAll(tasks);

        var lines = File.ReadAllLines(w.ConfigTxtPath);
        var includeCount = lines.Count(l => l.Trim().Equals(ApoWriter.IncludeLine, StringComparison.OrdinalIgnoreCase));
        _out.WriteLine("config.txt after concurrent EnsureInclude:\n" + string.Join("\n", lines));
        _out.WriteLine("per-task results: " + string.Join(", ", results));
        _out.WriteLine($"include line count: {includeCount}");
        Assert.Equal(1, includeCount);
        Assert.Equal(1, results.Count(r => r)); // exactly one task actually appended
    }

    [Fact]
    public void FormatPreamp_normalizes_values_rounding_to_zero()
    {
        var a = ApoWriter.FormatPreamp(-0.04);
        var b = ApoWriter.FormatPreamp(-0.0);
        _out.WriteLine($"-0.04 => {a}; -0.0 => {b}");
        Assert.Equal("Preamp: 0.0 dB", a);
        Assert.Equal("Preamp: 0.0 dB", b);
    }

    [Fact]
    public void IncludeGuard_restores_line_after_external_rewrite()
    {
        using var w = new ApoWriter(_dir);
        w.EnsureInclude();
        w.StartIncludeGuard();
        // simulate Peace rewriting config.txt and dropping our line
        File.WriteAllText(w.ConfigTxtPath, "Include: peace.txt\r\n");
        var restored = false;
        for (int i = 0; i < 40; i++) // up to 4 s for the watcher (real FS events)
        {
            Thread.Sleep(100);
            if (File.ReadAllLines(w.ConfigTxtPath).Any(l => l.Trim() == ApoWriter.IncludeLine))
            {
                restored = true;
                break;
            }
        }
        _out.WriteLine("config.txt after guard:\n" + File.ReadAllText(w.ConfigTxtPath));
        Assert.True(restored, "include guard should have re-added the include line");
    }
}
