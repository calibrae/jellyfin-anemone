using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.Ingest;

/// <summary>In-memory store of per-job ingest bearer tokens. See <see cref="IIngestTokenStore"/>.</summary>
public sealed class IngestTokenStore : IIngestTokenStore
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(string jobId, string targetDirectory, string filePrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        ArgumentException.ThrowIfNullOrEmpty(filePrefix);

        PruneExpired();

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var grant = new IngestGrant(jobId, targetDirectory, filePrefix);
        _entries[jobId] = new Entry(tokenBytes, grant, DateTimeOffset.UtcNow);

        return Base64UrlEncode(tokenBytes);
    }

    public bool TryValidate(string jobId, string bearerToken, out IngestGrant grant)
    {
        grant = null!;

        if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(bearerToken))
        {
            return false;
        }

        if (!_entries.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        byte[] providedBytes;
        try
        {
            providedBytes = Base64UrlDecode(bearerToken);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(providedBytes, entry.TokenBytes))
        {
            return false;
        }

        grant = entry.Grant;
        return true;
    }

    public void Revoke(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return;
        }

        _entries.TryRemove(jobId, out _);
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.IssuedAt < cutoff)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };

        return Convert.FromBase64String(s);
    }

    private sealed record Entry(byte[] TokenBytes, IngestGrant Grant, DateTimeOffset IssuedAt);
}
