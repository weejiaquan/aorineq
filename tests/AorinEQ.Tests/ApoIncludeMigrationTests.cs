using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The v3.0.0 rename inside Equalizer APO's config folder: apo-volume.txt → aorineq.txt
/// and the Include line that points at it. config.txt is the USER's file — these tests exist to
/// prove we touch exactly one line of it and nothing else, on real files in a real directory.</summary>
public class ApoIncludeMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public ApoIncludeMigrationTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string ConfigTxt => Path.Combine(_dir, "config.txt");
    private string LegacyFile => Path.Combine(_dir, ApoWriter.LegacyVolumeFileName);
    private string CurrentFile => Path.Combine(_dir, ApoWriter.VolumeFileName);

    private void WriteConfigTxt(params string[] lines) =>
        File.WriteAllText(ConfigTxt, string.Join("\r\n", lines) + "\r\n");

    // ---- the pure rewrite ----

    [Fact]
    public void RewriteLegacyInclude_replaces_our_line_where_it_stands()
    {
        // Position matters: EAPO applies includes in file order, so moving our block past the
        // user's own filters would silently re-order their signal chain.
        var result = ApoWriter.RewriteLegacyInclude(new[]
        {
            "Preamp: -3.0 dB",
            "Include: apo-volume.txt",
            "Include: peace.txt",
        });

        Assert.Equal(new[]
        {
            "Preamp: -3.0 dB",
            "Include: aorineq.txt",
            "Include: peace.txt",
        }, result);
    }

    [Fact]
    public void RewriteLegacyInclude_leaves_every_other_line_verbatim()
    {
        var original = new[]
        {
            "# the user's own comment",
            "",
            "   Preamp: -3.0 dB   ",
            "Filter 1: ON PK Fc 1000 Hz Gain -2 dB Q 1.0",
            "Include: apo-volume.txt",
            "GraphicEQ: 25 0; 40 0",
            "Device: all",
        };

        var result = ApoWriter.RewriteLegacyInclude(original);

        Assert.Equal(original.Length, result.Count);
        for (int i = 0; i < original.Length; i++)
        {
            if (i == 4) continue; // ours
            Assert.Equal(original[i], result[i]);
        }
    }

    [Theory]
    [InlineData("include: apo-volume.txt")]
    [InlineData("INCLUDE: APO-VOLUME.TXT")]
    [InlineData("   Include: apo-volume.txt   ")]
    public void RewriteLegacyInclude_matches_the_legacy_line_the_way_EnsureInclude_always_has(string line)
    {
        // Same trimmed, case-insensitive comparison EnsureInclude uses, or a line the app itself
        // would consider "already present" would survive the migration as a duplicate.
        Assert.Equal(new[] { ApoWriter.IncludeLine }, ApoWriter.RewriteLegacyInclude(new[] { line }));
    }

    [Fact]
    public void RewriteLegacyInclude_drops_the_legacy_line_when_the_new_one_is_already_there()
    {
        var result = ApoWriter.RewriteLegacyInclude(new[]
        {
            "Include: apo-volume.txt",
            "Include: aorineq.txt",
        });

        Assert.Equal(new[] { "Include: aorineq.txt" }, result);
    }

    [Fact]
    public void RewriteLegacyInclude_collapses_two_legacy_lines_into_one()
    {
        var result = ApoWriter.RewriteLegacyInclude(new[]
        {
            "Include: apo-volume.txt",
            "Preamp: 0.0 dB",
            "Include: apo-volume.txt",
        });

        Assert.Equal(new[] { "Include: aorineq.txt", "Preamp: 0.0 dB" }, result);
    }

    [Fact]
    public void RewriteLegacyInclude_is_the_identity_when_there_is_nothing_of_ours()
    {
        var original = new[] { "Include: peace.txt", "Preamp: -3.0 dB" };

        Assert.Equal(original, ApoWriter.RewriteLegacyInclude(original));
    }

    // ---- the migration itself ----

    [Fact]
    public void MigrateLegacyInclude_renames_the_file_and_repoints_config_txt()
    {
        File.WriteAllText(LegacyFile, "# managed by apo-volume - do not hand-edit\r\nPreamp: -20.2 dB\r\n");
        WriteConfigTxt("Include: peace.txt", "Include: apo-volume.txt");

        using var w = new ApoWriter(_dir);
        Assert.True(w.MigrateLegacyInclude());

        _out.WriteLine("config.txt after migration:\n" + File.ReadAllText(ConfigTxt));
        // The user's rendered state carried over rather than being regenerated from nothing.
        Assert.Contains("Preamp: -20.2 dB", File.ReadAllText(CurrentFile));
        Assert.Equal(new[] { "Include: peace.txt", "Include: aorineq.txt" }, File.ReadAllLines(ConfigTxt));
        // The stale file goes only once nothing points at it any more.
        Assert.False(File.Exists(LegacyFile));
    }

    [Fact]
    public void MigrateLegacyInclude_is_a_no_op_the_second_time()
    {
        File.WriteAllText(LegacyFile, "Preamp: -20.2 dB\r\n");
        WriteConfigTxt("Include: apo-volume.txt");

        using var w = new ApoWriter(_dir);
        Assert.True(w.MigrateLegacyInclude());
        var configAfterFirst = File.ReadAllBytes(ConfigTxt);
        var fileAfterFirst = File.ReadAllBytes(CurrentFile);

        Assert.False(w.MigrateLegacyInclude());

        Assert.Equal(configAfterFirst, File.ReadAllBytes(ConfigTxt));
        Assert.Equal(fileAfterFirst, File.ReadAllBytes(CurrentFile));
    }

    [Fact]
    public void MigrateLegacyInclude_never_overwrites_an_existing_aorineq_txt()
    {
        File.WriteAllText(LegacyFile, "Preamp: -40.0 dB\r\n");
        File.WriteAllText(CurrentFile, "Preamp: -20.2 dB\r\n");
        WriteConfigTxt("Include: apo-volume.txt");

        using var w = new ApoWriter(_dir);
        Assert.True(w.MigrateLegacyInclude());

        Assert.Equal("Preamp: -20.2 dB\r\n", File.ReadAllText(CurrentFile));
        Assert.False(File.Exists(LegacyFile));
    }

    [Fact]
    public void MigrateLegacyInclude_leaves_the_rest_of_the_users_config_txt_byte_identical()
    {
        File.WriteAllText(LegacyFile, "Preamp: 0.0 dB\r\n");
        var userLines = new[]
        {
            "# Peace's section",
            "Include: peace.txt",
            "Include: apo-volume.txt",
            "Filter 1: ON PK Fc 1000 Hz Gain -2 dB Q 1.0",
            "",
        };
        WriteConfigTxt(userLines);

        using var w = new ApoWriter(_dir);
        w.MigrateLegacyInclude();

        var after = File.ReadAllLines(ConfigTxt);
        Assert.Equal(userLines.Length, after.Length);
        for (int i = 0; i < userLines.Length; i++)
            Assert.Equal(i == 2 ? ApoWriter.IncludeLine : userLines[i], after[i]);
    }

    [Fact]
    public void MigrateLegacyInclude_does_nothing_on_a_machine_that_never_had_the_old_name()
    {
        WriteConfigTxt("Include: aorineq.txt");
        var before = File.ReadAllBytes(ConfigTxt);

        using var w = new ApoWriter(_dir);
        Assert.False(w.MigrateLegacyInclude());

        Assert.Equal(before, File.ReadAllBytes(ConfigTxt));
        Assert.False(File.Exists(CurrentFile)); // nothing invented out of thin air
    }

    [Fact]
    public void MigrateLegacyInclude_keeps_the_legacy_file_when_config_txt_cannot_be_rewritten()
    {
        // The interruption that matters: if the Include line can't move, the file it names must
        // still be there — deleting it first would leave EAPO including nothing at all.
        File.WriteAllText(LegacyFile, "Preamp: -20.2 dB\r\n");
        WriteConfigTxt("Include: apo-volume.txt");

        using var w = new ApoWriter(_dir);
        using (new FileStream(ConfigTxt, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => w.MigrateLegacyInclude());
        }

        Assert.True(File.Exists(LegacyFile));
        Assert.Equal(new[] { "Include: apo-volume.txt" }, File.ReadAllLines(ConfigTxt));

        // Once the lock clears the next attempt completes it.
        Assert.True(w.MigrateLegacyInclude());
        Assert.Equal(new[] { ApoWriter.IncludeLine }, File.ReadAllLines(ConfigTxt));
        Assert.False(File.Exists(LegacyFile));
    }

    [Fact]
    public void MigrateLegacyInclude_writes_config_txt_atomically()
    {
        // temp + rename in the same directory, exactly like the volume file — EAPO's own watcher
        // must never observe a half-written config.txt. Proven by the absence of any leftover.
        File.WriteAllText(LegacyFile, "Preamp: 0.0 dB\r\n");
        WriteConfigTxt("Include: apo-volume.txt");

        using var w = new ApoWriter(_dir);
        w.MigrateLegacyInclude();

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void The_managed_header_and_include_line_name_the_new_file()
    {
        Assert.Equal("aorineq.txt", ApoWriter.VolumeFileName);
        Assert.Equal("Include: aorineq.txt", ApoWriter.IncludeLine);
        Assert.Equal("apo-volume.txt", ApoWriter.LegacyVolumeFileName);
        Assert.Contains("AorinEQ", ApoWriter.ManagedHeader);
        Assert.DoesNotContain("apo-volume", ApoWriter.ManagedHeader);
    }
}
