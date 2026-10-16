using System.Reflection;
using System.Text.RegularExpressions;

namespace AorinEQ.Core;

/// <summary>What this build is: a release (stamped by publish.ps1) or a dev build (everything else).
/// The stamp is AssemblyMetadata written by Directory.Build.props.</summary>
public sealed record BuildInfo(bool IsDev, string Version, string? Commit, string? Branch, DateTimeOffset? BuiltAt)
{
    private const int ShortCommitLength = 7;

    /// <summary>A detached HEAD has a SHA where the branch name would be.</summary>
    private static readonly Regex DetachedSha = new("^[0-9a-f]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? ShortCommit => Commit is null || Commit.Length <= ShortCommitLength ? Commit : Commit[..ShortCommitLength];

    public static BuildInfo From(string? informationalVersion, string? kind, string? branch, DateTimeOffset? builtAt)
    {
        var info = informationalVersion ?? "";
        var plus = info.IndexOf('+');
        var isDev = !string.Equals(kind, "release", StringComparison.OrdinalIgnoreCase);
        var cleanBranch = string.IsNullOrWhiteSpace(branch) || DetachedSha.IsMatch(branch.Trim()) ? null : branch.Trim();
        return new BuildInfo(
            isDev,
            plus < 0 ? info : info[..plus],
            plus < 0 ? null : info[(plus + 1)..],
            cleanBranch,
            isDev ? builtAt : null);
    }

    public static BuildInfo Current { get; } = ReadCurrent();

    private static BuildInfo ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        string? Meta(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                            .FirstOrDefault(a => a.Key == key)?.Value;

        // Single-file publish has no assembly location; the build time is then simply unknown.
        DateTimeOffset? builtAt = string.IsNullOrEmpty(assembly.Location)
            ? null
            : new DateTimeOffset(File.GetLastWriteTimeUtc(assembly.Location), TimeSpan.Zero);

        return From(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Meta("BuildKind"), Meta("BuildBranch"), builtAt);
    }
}
