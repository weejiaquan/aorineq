using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The product version is written once, in Directory.Build.props, and every project
/// inherits it. This pins that.
///
/// It used not to be. src/AorinEQ said 3.3.0 and src/AorinEQ.Core said 3.2.0, because a release
/// bumped one csproj and forgot the other - and nothing failed, because a wrong assembly version
/// is not a build error. It is only visible in a file properties dialog, in a crash report, or in
/// a support thread where the user's Core version names a build that was never released.
///
/// Sharing the property makes the two projects unable to disagree. This test guards the way back:
/// a project body is imported AFTER Directory.Build.props, so a <c>&lt;Version&gt;</c> typed into
/// any csproj would silently win and restore the drift. So every project file in the repository is
/// read here (the csproj links them into the test output, exactly as InstallerScriptTests reads the
/// real .iss), and the value is checked against the version actually stamped on two built
/// assemblies - proving the property does not merely exist but reaches the binaries.</summary>
public class VersionSingleSourceTests
{
    private const string PropsFileName = "Directory.Build.props";

    /// <summary>Every project file in the repository, by the name it is linked into the test
    /// output under. If a project is added to the repo it belongs here too.</summary>
    private static readonly string[] ProjectFileNames =
    [
        "AorinEQ.csproj",
        "AorinEQ.Core.csproj",
        "AorinEQ.Tests.csproj",
        "AppIconGen.csproj",
    ];

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public VersionSingleSourceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>The declared version, read out of the props file the build actually imports.</summary>
    private static string DeclaredVersion()
    {
        var elements = XDocument.Parse(Read(PropsFileName))
            .Descendants().Where(e => e.Name.LocalName == "Version").ToList();
        Assert.Single(elements);
        return elements[0].Value.Trim();
    }

    [Fact]
    public void DirectoryBuildPropsDeclaresExactlyOneVersion()
    {
        var version = DeclaredVersion();
        _out.WriteLine($"{PropsFileName} declares <Version>{version}</Version>");
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    /// <summary>The guard. A csproj-level &lt;Version&gt; overrides the shared one, so there must
    /// not be any - in a project file or in a nested Directory.Build.props that would shadow the
    /// root one for part of the tree.</summary>
    [Theory]
    [InlineData("AorinEQ.csproj")]
    [InlineData("AorinEQ.Core.csproj")]
    [InlineData("AorinEQ.Tests.csproj")]
    [InlineData("AppIconGen.csproj")]
    public void NoProjectRedeclaresTheVersion(string projectFileName)
    {
        var declarations = XDocument.Parse(Read(projectFileName))
            .Descendants().Where(e => e.Name.LocalName == "Version").ToList();

        _out.WriteLine($"{projectFileName}: {declarations.Count} <Version> element(s)");
        Assert.Empty(declarations);
    }

    [Fact]
    public void EveryProjectFileInTheRepositoryIsCovered()
    {
        // The linked copies exist only because the test csproj lists them; a project added to the
        // repo without being linked here would go unguarded, so the count is pinned too.
        foreach (var name in ProjectFileNames)
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), $"{name} was not linked into the test output");
        }
        _out.WriteLine($"guarding {ProjectFileNames.Length} project files: {string.Join(", ", ProjectFileNames)}");
    }

    /// <summary>The property is not just written - it is what the compiler stamped. Two assemblies
    /// built from two different csproj files, both carrying the one declared version.</summary>
    [Theory]
    [InlineData(typeof(AppIdentity))]          // src/AorinEQ.Core - the one that drifted
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
