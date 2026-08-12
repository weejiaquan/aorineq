using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The product version is written once, in Directory.Build.props, and every project
/// inherits it. This pins that.
///
/// It used not to be. src/AorinEQ said 3.3.0 and src/AorinEQ.Core said 3.2.0, because a release
/// bumped one csproj and forgot the other - and nothing failed, because a wrong assembly version is
/// not a build error. It is only visible in a file properties dialog, in a crash report, or in a
/// support thread where the user's Core version names a build that was never released.
///
/// Sharing the property makes the two projects unable to disagree. This test guards the way back.
/// A project body is imported AFTER Directory.Build.props, so any version-bearing property typed
/// into a csproj would silently win: not just &lt;Version&gt;, but VersionPrefix, AssemblyVersion,
/// FileVersion and InformationalVersion, each of which overrides a different part of what the
/// version actually IS on the shipped binary. All of them are banned, in every project.
///
/// Unlike InstallerScriptTests and AppIconTests, which read shipped files linked into the test
/// output, this one walks the REPOSITORY. A list of project files fixed at build time cannot
/// express "no project, including one added next year, redeclares this" - and the fifth csproj
/// nobody remembers to add to the list is exactly the shape of the bug being guarded against.
/// (A hand-written AssemblyInfo.cs is not a hole: GenerateAssemblyInfo is on, so a duplicate
/// attribute is a compile error.)</summary>
public class VersionSingleSourceTests
{
    private const string PropsFileName = "Directory.Build.props";

    /// <summary>Every MSBuild property that changes a version the build stamps on an assembly.
    /// Version alone is not enough: FileVersion overrides what Explorer and publish.ps1 read, and
    /// InformationalVersion overrides ProductVersion, which is what identifies a shipped exe.</summary>
    private static readonly string[] BannedProperties =
    [
        "Version", "VersionPrefix", "VersionSuffix",
        "AssemblyVersion", "FileVersion", "InformationalVersion", "PackageVersion",
    ];

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AorinEQ.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException(
            $"could not find the repository root (a directory containing AorinEQ.slnx) above {AppContext.BaseDirectory}");
    }

    /// <summary>Every project file in the repository, found rather than listed.</summary>
    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static List<XElement> PropertyElements(string path, string name) =>
        XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")
            .ToList();

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public VersionSingleSourceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string DeclaredVersion()
    {
        var elements = PropertyElements(Path.Combine(RepoRoot, PropsFileName), "Version");
        Assert.Single(elements);
        return elements[0].Value.Trim();
    }

    [Fact]
    public void DirectoryBuildPropsDeclaresExactlyOneVersion()
    {
        var version = DeclaredVersion();
        _out.WriteLine($"{RepoRoot}\\{PropsFileName} declares <Version>{version}</Version>");
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    /// <summary>The guard, over every project in the repository and every property that could
    /// override the shared one.</summary>
    [Fact]
    public void NoProjectRedeclaresAnyVersionProperty()
    {
        var offences = new List<string>();
        var checkedProjects = 0;

        foreach (var project in ProjectFiles())
        {
            checkedProjects++;
            var relative = Path.GetRelativePath(RepoRoot, project);
            foreach (var property in BannedProperties)
            {
                foreach (var element in PropertyElements(project, property))
                {
                    offences.Add($"{relative} declares <{property}>{element.Value}</{property}>");
                }
            }
            _out.WriteLine($"checked {relative}");
        }

        Assert.True(checkedProjects >= 4, $"only found {checkedProjects} project files under {RepoRoot}");
        Assert.Empty(offences);
    }

    /// <summary>One props file, at the root. A nested Directory.Build.props shadows the root one
    /// for its whole subtree - MSBuild stops at the FIRST it finds walking up - so a second file
    /// would silently un-share the version for everything beneath it.</summary>
    [Fact]
    public void OnlyTheRootDirectoryBuildFileExists()
    {
        var buildFiles = new[] { "Directory.Build.props", "Directory.Build.targets" }
            .SelectMany(name => Directory.EnumerateFiles(RepoRoot, name, SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(RepoRoot, p))
            .ToList();

        _out.WriteLine($"build files: {string.Join(", ", buildFiles)}");
        Assert.Equal([PropsFileName], buildFiles);
    }

    /// <summary>The property is not just written - it is what the compiler stamped. Two assemblies
    /// built from two different csproj files, both carrying the one declared version.
    ///
    /// The third, src/AorinEQ, cannot be loaded here (the test project references Core and nothing
    /// else, deliberately). It is covered structurally by the property ban above, and at release
    /// time by the workflow, which refuses to publish unless the tag names the version stamped on
    /// the exe that was actually built.</summary>
    [Theory]
    [InlineData(typeof(AppIdentity))]              // src/AorinEQ.Core - the one that drifted
    [InlineData(typeof(VersionSingleSourceTests))] // tests/AorinEQ.Tests - a second, independent project
    public void BuiltAssembliesCarryTheDeclaredVersion(Type typeFromAssembly)
    {
        var declared = DeclaredVersion();
        var assembly = typeFromAssembly.Assembly;

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        _out.WriteLine($"{assembly.GetName().Name}: FileVersion={fileVersion} Informational={informational} declared={declared}");

        // The SDK stamps FileVersion as four parts ("3.3.0.0") and appends "+<commit>" to the
        // informational version, so both are compared on the three parts that are declared.
        Assert.Equal(declared, Regex.Replace(fileVersion, @"^(\d+\.\d+\.\d+)(\.\d+)?$", "$1"));
        Assert.StartsWith(declared, informational);
    }
}
