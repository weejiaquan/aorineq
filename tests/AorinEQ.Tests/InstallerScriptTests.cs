using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The Inno Setup script is shipped configuration, not code: nothing compiles it during a
/// normal build, and every property asserted here fails SILENTLY and in the field rather than
/// loudly at the desk.
///
/// A wrong install directory does not error - it quietly demotes every user's in-app update to the
/// "open the release page yourself" fallback, because UpdateApplier.CanWriteTo cannot write to
/// Program Files without elevation. A stale AppMutex does not error - Setup simply stops noticing
/// the running app and overwrites an exe someone is using. An installer-written Run key does not
/// error - it just becomes a third writer fighting Autostart and ScheduledTaskAutostart. And a
/// version literal typed in here does not error - it drifts from the csproj and Apps &amp; Features
/// starts naming a build nobody has.
///
/// So the script itself is read, exactly as AppIconTests reads the shipped .ico rather than a copy
/// (the csproj links the real file into the test output).</summary>
public class InstallerScriptTests
{
    private const string ScriptFileName = "AorinEQ.iss";

    private static readonly string Script =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ScriptFileName));

    /// <summary>The same script with its ";" comment lines removed. The header comments EXPLAIN
    /// what the installer must not do - they name Program Files, Equalizer APO and the URL scheme -
    /// so "the installer never does X" can only be asserted against the directives.</summary>
    private static readonly string Directives = string.Join(
        '\n', Script.Split('\n').Where(line => !line.TrimStart().StartsWith(';')));

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public InstallerScriptTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>Reads a <c>Key=Value</c> directive out of the script's [Setup] section. Inno keys
    /// are case-insensitive and sit at the start of their line.</summary>
    private static string? Directive(string key)
    {
        var match = Regex.Match(
            Script, $@"^{Regex.Escape(key)}=(.*)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [Fact]
    public void ScriptShipsWithTheRepository()
    {
        Assert.False(string.IsNullOrWhiteSpace(Script));
        _out.WriteLine($"{ScriptFileName} is {Script.Length} characters");
    }

    /// <summary>The one value that has to be spelled twice - once in C#, once in Inno's own
    /// syntax - because Inno cannot read a C# constant. This is the seam that binds them.</summary>
    [Fact]
    public void AppMutexIsTheAppsRealSingleInstanceMutex()
    {
        var mutex = Directive("AppMutex");
        _out.WriteLine($"AppMutex={mutex}, AppIdentity.SingleInstanceMutexName={AppIdentity.SingleInstanceMutexName}");
        Assert.Equal(AppIdentity.SingleInstanceMutexName, mutex);
    }

    /// <summary>Per-user, unelevated, and writable by the person who installed it - which is
    /// precisely the property the in-app updater's in-place exe swap depends on.</summary>
    [Fact]
    public void InstallsPerUserWithoutElevation()
    {
        Assert.Equal("lowest", Directive("PrivilegesRequired"));

        var dir = Directive("DefaultDirName");
        _out.WriteLine($"DefaultDirName={dir}");
        Assert.Equal(@"{localappdata}\Programs\{#AppName}", dir);
        Assert.DoesNotContain("{pf", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{commonpf", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Program Files", Directives, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A fixed GUID is what makes the next version REPLACE this install instead of
    /// stacking a second copy beside it with its own Apps &amp; Features entry.</summary>
    [Fact]
    public void AppIdIsAFixedGuid()
    {
        var appId = Directive("AppId");
        _out.WriteLine($"AppId={appId}");
        // Inno escapes a leading brace by doubling it, so the literal GUID starts at index 1.
        Assert.Matches(@"^\{\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", appId);
    }

    /// <summary>The csproj's &lt;Version&gt; is the single source of truth: the SDK stamps it onto
    /// the exe, and the script reads it back off that exe. Typing a literal here is the drift this
    /// forbids.</summary>
    [Fact]
    public void VersionIsDerivedFromTheExeItPackagesRatherThanTypedIn()
    {
        Assert.Equal("{#AppVersion}", Directive("AppVersion"));
        Assert.Equal("{#AppVersion}", Directive("VersionInfoVersion"));
        Assert.Contains("GetVersionComponents(AppExePath", Script);

        // No "AppVersion" definition that is a literal version number.
        Assert.DoesNotMatch(new Regex(@"^\s*#define\s+AppVersion\s+""?\d", RegexOptions.Multiline), Script);
    }

    /// <summary>Autostart belongs to the app: Settings picks between an HKCU Run value and a
    /// scheduled task depending on RunAsAdmin, and reconciles them. The installer writing either
    /// one would be a third writer with no idea what the other two decided.</summary>
    [Fact]
    public void WritesNoAutostartEntryAndNoRegistryAtAll()
    {
        Assert.DoesNotContain("[Registry]", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"CurrentVersion\Run", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schtasks", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{userstartup}", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{commonstartup}", Directives, StringComparison.OrdinalIgnoreCase);

        // ...and the finish page says so, since that is where a first-time user looks.
        Assert.Contains("Start with Windows", Directive("FinishedLabel")!);
    }

    /// <summary>Equalizer APO's config and the aorineq:// scheme are created, migrated and
    /// re-pointed by the running app. An installer touching either would fight it - and on
    /// uninstall would break a second, portable copy that currently owns the registration.</summary>
    [Fact]
    public void LeavesEqualizerApoAndTheUrlSchemeToTheApp()
    {
        Assert.DoesNotContain("EqualizerAPO", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("config.txt", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aorineq:", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("URL Protocol", Directives, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"Software\Classes", Directives, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The user's skins are artwork no reinstall can bring back, so uninstall ASKS - and
    /// answers "keep" for itself when message boxes are suppressed (silent uninstall), which is
    /// exactly what SuppressibleMsgBox's default argument does.</summary>
    [Fact]
    public void UninstallAsksAboutUserDataAndDefaultsToKeepingIt()
    {
        Assert.Contains(@"ExpandConstant('{userappdata}\{#AppName}')", Script);
        Assert.Contains("SuppressibleMsgBox(", Script);
        Assert.Contains("MB_YESNO, IDYES) = IDNO then", Script);

        // Exactly one deletion of that folder, and it is the one gated on an explicit "No".
        Assert.Single(Regex.Matches(Script, @"DelTree\("));
    }

    /// <summary>The portable pair keeps its exact names because the shipped updater and the
    /// website depend on them; the installer is additive.</summary>
    [Fact]
    public void ShipsTheSamePortableExeUnderItsContractedName()
    {
        Assert.Contains(@"#define AppExeName ""AorinEQ.exe""", Script);
        Assert.Contains(@"Source: ""{#AppExePath}""; DestDir: ""{app}""; Flags: ignoreversion", Script);
        Assert.Equal("AorinEQ-Setup", Directive("OutputBaseFilename"));
    }

    /// <summary>A Start Menu shortcut always; a desktop one only if asked for.</summary>
    [Fact]
    public void CreatesAStartMenuShortcutAndOffersTheDesktopOneUnchecked()
    {
        Assert.Contains(@"Name: ""{group}\{#AppName}""; Filename: ""{app}\{#AppExeName}""", Script);

        var desktop = Regex.Match(Script, @"^Name: ""\{userdesktop\}.*$", RegexOptions.Multiline).Value;
        _out.WriteLine(desktop);
        Assert.Contains("Tasks: desktopicon", desktop);
        Assert.Matches(new Regex(@"^Name: ""desktopicon"";.*Flags: unchecked\s*$", RegexOptions.Multiline), Script);
    }

    /// <summary>UpdateApplier renames the running exe to AorinEQ.exe.old before moving the
    /// downloaded one in. An install or uninstall that ignored it could leave 74 MB behind.</summary>
    [Fact]
    public void CleansUpTheFileTheInPlaceUpdaterLeavesBehind()
    {
        foreach (var section in new[] { "[InstallDelete]", "[UninstallDelete]" })
        {
            var start = Script.IndexOf(section, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{section} missing");
            var body = Script[start..];
            var end = body.IndexOf("\n[", StringComparison.Ordinal);
            if (end >= 0) body = body[..end];
            Assert.Contains(@"Type: files; Name: ""{app}\{#AppExeName}.old""", body);
        }
    }
}
