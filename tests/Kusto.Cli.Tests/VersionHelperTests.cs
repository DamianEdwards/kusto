using NuGet.Versioning;

namespace Kusto.Cli.Tests;

public sealed class VersionHelperTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.3.1-pre.1.dev.2", "0.3.1-pre.1.dev.2")]
    public void TryParse_AcceptsReleaseTags(string input, string expected)
    {
        Assert.True(VersionHelper.TryParse(input, out var version));
        Assert.Equal(expected, version.ToNormalizedString());
    }

    [Fact]
    public void IsUpdateCandidate_StableDefaultsToStrictStable()
    {
        var current = NuGetVersion.Parse("1.0.0");

        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
        Assert.False(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: false));
        Assert.False(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0-pre.1.dev.1"),
            allowPreRelease: true));
    }

    [Fact]
    public void IsUpdateCandidate_PreReleaseCanAdvanceToStable()
    {
        var current = NuGetVersion.Parse("1.1.0-pre.1.rel");

        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
    }

    [Fact]
    public void IsUpdateCandidate_DevCanAdvanceToAnyNewerChannel()
    {
        var current = NuGetVersion.Parse("1.1.0-pre.1.dev.2");

        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: false));
        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
    }

    [Fact]
    public void IsUpdateCandidate_StableOnlyRejectsNewerPreview()
    {
        Assert.False(VersionHelper.IsUpdateCandidate(
            NuGetVersion.Parse("1.0.0-pre.1.rel"),
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: true,
            stableOnly: true));
    }
}
