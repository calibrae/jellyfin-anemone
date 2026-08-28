using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Plugin.Anemone.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using IoFile = System.IO.File;

namespace Jellyfin.Plugin.Anemone.Ingest;

/// <summary>
/// Receives HLS segments/playlists ffmpeg on an agent PUTs back (chunked, no Content-Length,
/// <c>-http_persistent 1</c>). Writes atomically via <c>&lt;name&gt;.part</c> + rename. See PROTOCOL.md
/// "Ingest" and research/ffmpeg-network-io.md §1.
/// </summary>
[ApiController]
[Route("Anemone/ingest")]
[AllowAnonymous]
public sealed class IngestController : ControllerBase
{
    private const int CopyBufferBytes = 64 * 1024;

    // First-playlist-per-job Information log, process-lifetime, best-effort (not persisted).
    private static readonly ConcurrentDictionary<string, bool> PlaylistLogged = new(StringComparer.Ordinal);

    private readonly IIngestTokenStore _tokens;
    private readonly ILogger<IngestController> _logger;

    public IngestController(IIngestTokenStore tokens, ILogger<IngestController> logger)
    {
        _tokens = tokens;
        _logger = logger;
    }

    [HttpPut("{jobId}/{name}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Put(string jobId, string name)
    {
        if (!TryGetBearerToken(out var token) || !_tokens.TryValidate(jobId, token, out var grant))
        {
            _logger.LogDebug("anemone: ingest rejected (bad token/job) job={JobId} name={Name}", jobId, name);
            return Fail(StatusCodes.Status403Forbidden);
        }

        if (!IngestNames.IsValid(grant.FilePrefix, name))
        {
            _logger.LogDebug("anemone: ingest rejected (bad filename) job={JobId} name={Name} prefix={Prefix}", jobId, name, grant.FilePrefix);
            return Fail(StatusCodes.Status404NotFound);
        }

        var finalPath = Path.Combine(grant.TargetDirectory, name);
        var partPath = finalPath + ".part";
        var sw = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(grant.TargetDirectory);
            await using (var fs = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.Asynchronous))
            {
                await Request.Body.CopyToAsync(fs, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            IoFile.Move(partPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "anemone: ingest write failed job={JobId} name={Name}", jobId, name);
            TryDelete(partPath);
            return Fail(StatusCodes.Status404NotFound);
        }

        sw.Stop();
        var bytes = TryGetFileLength(finalPath);
        _logger.LogDebug(
            "anemone: ingest wrote job={JobId} name={Name} bytes={Bytes} elapsedMs={ElapsedMs}",
            jobId,
            name,
            bytes,
            sw.ElapsedMilliseconds);

        if (name.EndsWith(".m3u8", StringComparison.Ordinal) && PlaylistLogged.TryAdd(jobId, true))
        {
            _logger.LogInformation("anemone: first playlist upload for job {JobId}", jobId);
        }

        // Keep-alive (-http_persistent 1) must keep working: don't abort on success.
        return StatusCode(StatusCodes.Status201Created);
    }

    private IActionResult Fail(int statusCode)
    {
        Response.StatusCode = statusCode;

        // ffmpeg ignores HTTP status codes on PUT; drop the connection so the failure is visible on the agent side.
        HttpContext.Abort();
        return new EmptyResult();
    }

    private bool TryGetBearerToken(out string token)
    {
        token = string.Empty;
        var header = Request.Headers.Authorization.ToString();
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
