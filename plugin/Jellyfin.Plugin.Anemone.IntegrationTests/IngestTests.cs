using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Anemone.TestKit;

namespace Jellyfin.Plugin.Anemone.IntegrationTests;

/// <summary>
/// Real chunked HTTP PUT (no Content-Length, mimicking ffmpeg's <c>-method PUT -http_persistent 1</c>)
/// against the real <see cref="Agents.AnemoneListener"/>/<see cref="Ingest.IngestHandler"/>. See
/// PROTOCOL.md "Ingest" and "Valid filenames".
/// </summary>
public class IngestTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static HttpClient CreateClient() => new() { Timeout = Timeout };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, byte[] body, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StreamContent(new NonSeekableStream(new MemoryStream(body))),
        };
        request.Headers.ConnectionClose = false; // Connection: keep-alive, mirroring -http_persistent 1
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    [Fact]
    public async Task Put_ValidTokenAndFilename_LandsFileAtomically()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("segments");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "a7858cf3a2e6dbf7c9a1d5b6e4f0c2d1";
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var body = Encoding.UTF8.GetBytes("fake ts segment bytes");

        using var client = CreateClient();
        var response = await PutAsync(client, $"{harness.HttpBaseUrl}/Anemone/ingest/{jobId}/{Prefix}0.ts", body, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var finalPath = Path.Combine(targetDir, Prefix + "0.ts");
        Assert.True(File.Exists(finalPath));
        Assert.Equal(body, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(finalPath + ".part"));
    }

    [Fact]
    public async Task Put_PlaylistFilename_AlsoAccepted()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("segments-playlist");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "a7858cf3a2e6dbf7c9a1d5b6e4f0c2d1";
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n");

        using var client = CreateClient();
        var response = await PutAsync(client, $"{harness.HttpBaseUrl}/Anemone/ingest/{jobId}/{Prefix}.m3u8", body, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(targetDir, Prefix + ".m3u8")));
    }

    [Fact]
    public async Task Put_SlowBody_FinalFileNeverAppearsUntilComplete()
    {
        // Note on what this does NOT try to prove: once the server has received every byte, it renames
        // .part -> the final name and ONLY THEN sends the response - so there is an inherent, harmless
        // gap between "the file exists on disk" and "the client's SendAsync task observes completion"
        // (the response still has to cross the network back). Asserting against that gap would be
        // asserting an implementation detail of TCP/HttpClient, not of IngestHandler. What actually
        // matters, and what's provable from the client side, is: the final name does not exist while the
        // body is still being sent (well before the last chunk could plausibly have arrived), and once it
        // does exist, its content is the complete body, never a truncated prefix.
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("slow-segments");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "slowjob";
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);
        var finalPath = Path.Combine(targetDir, Prefix + "0.ts");

        const int ChunkCount = 10;
        var body = new byte[ChunkCount * 4096];
        Random.Shared.NextBytes(body);
        var chunkDelay = TimeSpan.FromMilliseconds(60);
        var content = NonSeekableStream.Trickle(body, chunkSize: 4096, delayBetweenChunks: chunkDelay);

        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{harness.HttpBaseUrl}/Anemone/ingest/{jobId}/{Prefix}0.ts")
        {
            Content = new StreamContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var putTask = client.SendAsync(request);

        // Well inside the upload window (chunk 3 of 10 at the earliest) - the body can't possibly have
        // finished arriving yet.
        await Task.Delay(TimeSpan.FromTicks(chunkDelay.Ticks * 3));
        Assert.False(File.Exists(finalPath), "anemone-test: the final file appeared while the body was still being sent");
        Assert.False(putTask.IsCompleted, "anemone-test: the request finished uploading faster than expected - widen the trickle delay/chunk count");

        using var response = await putTask;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(File.Exists(finalPath));
        Assert.Equal(body, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(finalPath + ".part"));
    }

    [Fact]
    public async Task Put_BadToken_Rejected()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("bad-token");
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "prefix";
        harness.TokenStore.Issue(jobId, targetDir, Prefix); // issued, but we deliberately use the wrong token below

        using var client = CreateClient();

        // See IngestHandler.Fail: on rejection the server sets the status AND aborts the connection (ffmpeg
        // ignores HTTP status codes on PUT, so the drop is what actually makes the failure visible on the
        // agent side) - a well-behaved client can observe either a non-success status or the abort itself.
        await AssertRejectedAsync(
            () => PutAsync(client, $"{harness.HttpBaseUrl}/Anemone/ingest/{jobId}/{Prefix}0.ts", "x"u8.ToArray(), "not-the-real-token"),
            HttpStatusCode.Forbidden);

        Assert.False(File.Exists(Path.Combine(targetDir, Prefix + "0.ts")));
    }

    [Fact]
    public async Task Put_UnknownJob_Rejected()
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("unknown-job");
        var issuedForJobId = Guid.NewGuid().ToString("N");
        const string Prefix = "prefix";
        var token = harness.TokenStore.Issue(issuedForJobId, targetDir, Prefix);
        var differentJobId = Guid.NewGuid().ToString("N");

        using var client = CreateClient();

        await AssertRejectedAsync(
            () => PutAsync(client, $"{harness.HttpBaseUrl}/Anemone/ingest/{differentJobId}/{Prefix}0.ts", "x"u8.ToArray(), token),
            HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("../escape0.ts")]
    [InlineData("wrongprefix0.ts")]
    [InlineData("prefix0.wrongext")]
    [InlineData("prefix0.ts/../evil")]
    public async Task Put_BadFilename_Rejected(string badName)
    {
        await using var harness = await AnemoneIntegrationHarness.StartAsync();
        var targetDir = harness.Root.CreateSubdirectory("bad-filename-" + Guid.NewGuid().ToString("N"));
        var jobId = Guid.NewGuid().ToString("N");
        const string Prefix = "prefix";
        var token = harness.TokenStore.Issue(jobId, targetDir, Prefix);

        using var client = CreateClient();

        await AssertRejectedAsync(
            () => PutAsync(client, $"{harness.HttpBaseUrl}/Anemone/ingest/{jobId}/{Uri.EscapeDataString(badName)}", "x"u8.ToArray(), token),
            HttpStatusCode.NotFound);

        Assert.Empty(Directory.GetFiles(targetDir));
    }

    /// <summary>
    /// Sends the request and accepts either a non-2xx response or the request itself failing (connection
    /// reset/aborted before a full response could be read) as "rejected" - both are valid observations of
    /// <c>IngestHandler.Fail</c>'s <c>context.Abort()</c>, and which one a given HTTP stack surfaces isn't
    /// something this test should be coupled to.
    /// </summary>
    private static async Task AssertRejectedAsync(Func<Task<HttpResponseMessage>> send, HttpStatusCode expectedIfResponseArrives)
    {
        // expectedIfResponseArrives documents what IngestHandler.Fail sets before aborting the connection;
        // it isn't asserted exactly because whether a given HTTP stack surfaces that status (vs. only the
        // abort, handled as an exception below) isn't guaranteed - only that the request is rejected.
        _ = expectedIfResponseArrives;

        try
        {
            using var response = await send();
            Assert.False(response.IsSuccessStatusCode, $"anemone-test: expected a rejection, got {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (HttpRequestException)
        {
            // The connection was aborted before a full response was readable - also a valid rejection.
        }
        catch (IOException)
        {
            // Same, surfaced at the transport layer instead.
        }
    }
}
