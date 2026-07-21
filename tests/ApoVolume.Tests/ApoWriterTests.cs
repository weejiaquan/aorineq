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
    public void WriteVolume_creates_file_immediately_with_preamp_line()
    {
        using var w = new ApoWriter(_dir);
        w.WriteVolume(-25.2525);
        // leading edge is synchronous: file must exist right now
        var content = File.ReadAllText(w.VolumeFilePath);
        _out.WriteLine("file content: " + content.TrimEnd());
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
