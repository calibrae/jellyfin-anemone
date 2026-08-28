using Jellyfin.Plugin.Cluster.Ingest;

namespace Jellyfin.Plugin.Cluster.Tests.Ingest;

public class IngestTokenStoreTests
{
    [Fact]
    public void IssueThenValidate_Succeeds()
    {
        var store = new IngestTokenStore();
        var token = store.Issue("job-1", "/tmp/transcodes", "a7858c");

        var ok = store.TryValidate("job-1", token, out var grant);

        Assert.True(ok);
        Assert.Equal("job-1", grant.JobId);
        Assert.Equal("/tmp/transcodes", grant.TargetDirectory);
        Assert.Equal("a7858c", grant.FilePrefix);
    }

    [Fact]
    public void Validate_WrongToken_Fails()
    {
        var store = new IngestTokenStore();
        store.Issue("job-1", "/tmp/transcodes", "a7858c");

        var ok = store.TryValidate("job-1", "not-the-real-token", out _);

        Assert.False(ok);
    }

    [Fact]
    public void Validate_WrongJob_Fails()
    {
        var store = new IngestTokenStore();
        var token = store.Issue("job-1", "/tmp/transcodes", "a7858c");

        var ok = store.TryValidate("job-2", token, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Revoke_ThenValidate_Fails()
    {
        var store = new IngestTokenStore();
        var token = store.Issue("job-1", "/tmp/transcodes", "a7858c");

        store.Revoke("job-1");
        var ok = store.TryValidate("job-1", token, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Validate_MalformedToken_DoesNotThrow()
    {
        var store = new IngestTokenStore();
        store.Issue("job-1", "/tmp/transcodes", "a7858c");

        var ok = store.TryValidate("job-1", "not-valid-base64url!!!", out _);

        Assert.False(ok);
    }

    [Fact]
    public void Issue_ReturnsDistinctTokensPerJob()
    {
        var store = new IngestTokenStore();
        var t1 = store.Issue("job-1", "/tmp/transcodes", "a7858c");
        var t2 = store.Issue("job-2", "/tmp/transcodes", "b1234d");

        Assert.NotEqual(t1, t2);
        Assert.True(store.TryValidate("job-1", t1, out _));
        Assert.True(store.TryValidate("job-2", t2, out _));
    }
}
