using System.Reflection;
using System.Text.RegularExpressions;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class BuildInfoTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T = new(2026, 10, 16, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void A_release_stamp_is_not_dev()
    {
        var info = BuildInfo.From("3.8.0+abc", "release", "master", T);
        output.WriteLine(info.ToString());
        Assert.False(info.IsDev);
        Assert.Null(info.BuiltAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("Release-candidate")]
    public void Anything_else_is_dev(string? kind)
    {
        var info = BuildInfo.From("3.8.0+abc", kind, "feat/x", T);
        output.WriteLine($"kind='{kind}' -> {info}");
        Assert.True(info.IsDev);
        Assert.Equal(T, info.BuiltAt);
        Assert.Equal("feat/x", info.Branch);
    }

    [Fact]
    public void Release_kind_is_case_insensitive()
    {
        Assert.False(BuildInfo.From("3.8.0", "RELEASE", null, T).IsDev);
    }

    [Fact]
    public void Version_and_commit_split_on_the_plus()
    {
        var info = BuildInfo.From("3.8.0+0123456789abcdef", "dev", null, null);
        output.WriteLine(info.ToString());
        Assert.Equal("3.8.0", info.Version);
        Assert.Equal("0123456789abcdef", info.Commit);
    }

    [Fact]
    public void ShortCommit_is_seven_characters()
    {
        Assert.Equal("0123456", BuildInfo.From("3.8.0+0123456789abcdef", "dev", null, null).ShortCommit);
        Assert.Equal("abc", BuildInfo.From("3.8.0+abc", "dev", null, null).ShortCommit);
    }

    [Fact]
    public void No_plus_means_no_commit()
    {
        var info = BuildInfo.From("3.8.0", "dev", null, null);
        Assert.Equal("3.8.0", info.Version);
        Assert.Null(info.Commit);
        Assert.Null(info.ShortCommit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void A_blank_or_detached_branch_is_no_branch(string? branch)
    {
        Assert.Null(BuildInfo.From("3.8.0", "dev", branch, null).Branch);
    }

    [Fact]
    public void Current_does_not_throw_and_reads_this_assemblys_version()
    {
        var info = BuildInfo.Current;
        output.WriteLine(info.ToString());
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    [Fact]
    public void This_test_build_is_stamped_dev_with_its_branch()
    {
        var meta = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                    .ToDictionary(a => a.Key, a => a.Value);
        meta.TryGetValue("BuildKind", out var kind);
        meta.TryGetValue("BuildBranch", out var branch);

        // Same rule as Directory.Build.props: no .git/HEAD file (worktree, missing) or a detached
        // 40-hex SHA means no branch; otherwise the name after "ref: refs/heads/".
        var head = Path.Combine(RepoFiles.Root, ".git", "HEAD");
        string? expected = null;
        if (File.Exists(head))
        {
            var text = File.ReadAllText(head).Trim();
            if (!Regex.IsMatch(text, "^[0-9a-f]{40}$") && text.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                expected = text["ref: refs/heads/".Length..];
        }

        output.WriteLine($"BuildKind='{kind}' BuildBranch='{branch}' expected branch='{expected}'");
        Assert.Equal("dev", kind);
        Assert.Equal(string.IsNullOrEmpty(expected) ? null : expected,
                     string.IsNullOrEmpty(branch) ? null : branch);
    }
}
