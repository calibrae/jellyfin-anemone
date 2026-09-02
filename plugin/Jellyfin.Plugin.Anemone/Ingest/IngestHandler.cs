using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Plugin.Anemone.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using IoFile = System.IO.File;

namespace Jellyfin.Plugin.Anemone.Ingest;

/// <summary>
/// Receives the HLS segments/playlists ffmpeg on an agent PUTs back (chunked, no Content-Length,
/// <c>-http_persistent 1</c>) and writes them atomically via <c>&lt;name&gt;.part</c> + rename, so Jellyfin's
/// segment-readiness check never sees a partial file. See PROTOCOL.md "Ingest".
/// </summary>
/// <remarks>
/// Transport-agnostic on purpose: the plugin serves this from its own Kestrel listener
/// (<see cref="Agents.AnemoneListener"/>), because Jellyfin's pipeline applies its own auth and a
/// 30 MB body cap to anything hosted inside the main server.
/// </remarks>
public sealed class IngestHandler
{
    private const int CopyBufferBytes = 64 * 1024;

    private static readonly ConcurrentDictionary<string, bool> PlaylistLogged = new(StringComparer.Ordinal);

    private readonly IIngestTokenStore _tokens;
    private readonly ILogger<IngestHandler> _logger;

    public IngestHandler(IIngestTokenStore tokens, ILogger<IngestHandler> logger)
    {
        _tokens = tokens;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context, string jobId, string name)
    {
        if (!TryGetBearerToken(context, out var token) || !_tokens.TryValidate(jobId, token, out var grant))
        {
            _logger.LogDebug("anemone: ingest rejected (bad token/job) job={JobId} name={Name}", jobId, name);
            Fail(context, StatusCodes.Status403Forbidden);
            return;
        }

        if (!IngestNames.IsValid(grant.FilePrefix, name))
        {
            _logger.LogDebug("anemone: ingest rejected (bad filename) job={JobId} name={Name} prefix={Prefix}", jobId, name, grant.FilePrefix);
            Fail(context, StatusCodes.Status404NotFound);
            return;
        }

        var finalPath = Path.Combine(grant.TargetDirectory, name);
        var partPath = finalPath + ".part";
        var sw = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(grant.TargetDirectory);
            await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                // anemone: deliberately NOT context.RequestAborted. PROTOCOL.md is explicit that ffmpeg
                // "ignores HTTP status codes on PUT" - it writes the chunked body and moves on to the next
                // segment without ever reading our response, often closing its end of the socket before
                // we've had a chance to answer. Over a real network link that race resolves itself (there's
                // enough latency for CopyToAsync to drain the already-fully-received body first), but
                // measured live over loopback: Kestrel can fire RequestAborted from that early close before
                // CopyToAsync has drained data that already arrived complete and intact, which would abort
                // a perfectly good upload for a reason that has nothing to do with the bytes themselves.
                // A genuinely dropped/incomplete connection still surfaces as IOException from the pipe
                // itself (caught below) with no token needed to detect it.
                await context.Request.Body.CopyToAsync(fs, CancellationToken.None).ConfigureAwait(false);
            }

            IoFile.Move(partPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "anemone: ingest write failed job={JobId} name={Name}", jobId, name);
            TryDelete(partPath);
            Fail(context, StatusCodes.Status404NotFound);
            return;
        }

        sw.Stop();
        _logger.LogDebug(
            "anemone: ingest wrote job={JobId} name={Name} bytes={Bytes} elapsedMs={ElapsedMs}",
            jobId,
            name,
            TryGetFileLength(finalPath),
            sw.ElapsedMilliseconds);

        if (name.EndsWith(".m3u8", StringComparison.Ordinal) && PlaylistLogged.TryAdd(jobId, true))
        {
            _logger.LogInformation("anemone: first playlist upload for job {JobId}", jobId);
        }

        // Keep-alive (-http_persistent 1) must keep working: don't abort on success.
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    private static void Fail(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;

        // ffmpeg ignores HTTP status codes on PUT; drop the connection so the failure is visible agent-side.
        context.Abort();
    }

    private static bool TryGetBearerToken(HttpContext context, out string token)
    {
        token = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        const string Prefix = "Bearer ";
        if (header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            token = header[Prefix.Length..];
        }

        return !string.IsNullOrEmpty(token);
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            IoFile.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
