using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The startup sweep of the designer's temp folders, run against a temp root of its own
/// (never the real %TEMP%\aorineq-designer, which a running designer may be using).</summary>
public class DesignerTempFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aorineq-sweep-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public DesignerTempFoldersTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), file);
        return folder;
    }

    [Fact]
    public void Stale_folders_go_and_one_with_a_file_in_use_stays_whole()
    {
        var empty = Folder("empty");
        var stale = Folder("stale", "sheet.png", Path.Combine("nested", "skin.json"));
        var held = Folder("held", "free.png", Path.Combine("nested", "open.png"));
        var loose = Path.Combine(_root, "loose.txt"); // not a session folder: left alone
        File.WriteAllText(loose, "x");
        using (new FileStream(Path.Combine(held, "nested", "open.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
            DesignerTempFolders.DeleteStale(_root);
        var left = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_root, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        _out.WriteLine($"expected left: held, held\\free.png, held\\nested, held\\nested\\open.png, loose.txt; actual {string.Join(", ", left)}");
        Assert.False(Directory.Exists(empty));
        Assert.False(Directory.Exists(stale));
        Assert.Equal(new[] { "held", Path.Combine("held", "free.png"), Path.Combine("held", "nested"),
            Path.Combine("held", "nested", "open.png"), "loose.txt" }, left);
    }

    [Fact]
    public void A_missing_root_is_nothing_to_sweep()
    {
        var missing = Path.Combine(_root, "never-made");
        var ex = Record.Exception(() => DesignerTempFolders.DeleteStale(missing));
        _out.WriteLine($"expected no exception and no folder; actual {ex?.GetType().Name ?? "none"}, exists {Directory.Exists(missing)}");
        Assert.Null(ex);
        Assert.False(Directory.Exists(missing));
    }
}
