namespace AorinEQ.Tests;

/// <summary>Repository files that tests read directly.
///
/// Walks up from the test binary to the directory holding AorinEQ.slnx, the way
/// VersionSingleSourceTests does. Reading the REPOSITORY rather than a copy linked at build time
/// is deliberate for the same reason it is there: a list fixed at build time cannot express "every
/// window, including the one added next year", and the window nobody remembered to add to the list
/// is exactly the shape of the bug these tests guard against.</summary>
public static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    /// <summary>Every window and dialog XAML in the app. Discovered, not listed.</summary>
    public static IReadOnlyList<string> WindowXaml { get; } =
        Directory.GetFiles(Path.Combine(Root, "src", "AorinEQ", "UI"), "*.xaml")
                 .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToList();

    /// <summary>Every C# file in the app project, obj/ excluded. Used to find Loc.T call sites.</summary>
    public static IReadOnlyList<string> AppSources { get; } =
        Directory.GetFiles(Path.Combine(Root, "src", "AorinEQ"), "*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToList();

    public static string ReadText(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>xunit needs the file list as TheoryData so a failure names the file it came from,
    /// rather than one test failing for eleven possible reasons.</summary>
    public static TheoryData<string> WindowXamlTheoryData()
    {
        var data = new TheoryData<string>();
        foreach (var path in WindowXaml) data.Add(path);
        return data;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AorinEQ.slnx")))
                return dir.FullName;

        throw new InvalidOperationException(
            "Could not find the repository root (a directory containing AorinEQ.slnx) above " +
            AppContext.BaseDirectory);
    }
}
