using Jellyfin.Plugin.Cluster.Ingest;

namespace Jellyfin.Plugin.Cluster.Tests.Ingest;

public class IngestNamesTests
{
    [Theory]
    [InlineData("a7858c0.ts")]
    [InlineData("a7858c123.ts")]
    [InlineData("a7858c-1.mp4")]
    [InlineData("a7858c.m3u8")]
    [InlineData("a7858c0.mp4")]
    [InlineData("a7858c0.m4s")]
    public void IsValid_AcceptsExpectedNames(string name)
    {
        Assert.True(IngestNames.IsValid("a7858c", name));
    }

    [Theory]
    [InlineData("a7858c.part")]
    [InlineData("../x.ts")]
    [InlineData("b7858c0.ts")]
    [InlineData("a7858c0.ts/..")]
    [InlineData("a7858c0.TS")]
    [InlineData("")]
    [InlineData("a7858c")]
    [InlineData("a7858c0.tsx")]
    public void IsValid_RejectsUnexpectedNames(string name)
    {
        Assert.False(IngestNames.IsValid("a7858c", name));
    }

    [Fact]
    public void IsValid_RejectsEmptyPrefix()
    {
        Assert.False(IngestNames.IsValid(string.Empty, "0.ts"));
    }

    [Fact]
    public void IsValid_PrefixIsRegexEscaped()
    {
        // A prefix containing regex metacharacters must be treated literally, not as a pattern.
        Assert.False(IngestNames.IsValid("a.b", "aXb0.ts"));
        Assert.True(IngestNames.IsValid("a.b", "a.b0.ts"));
    }
}
