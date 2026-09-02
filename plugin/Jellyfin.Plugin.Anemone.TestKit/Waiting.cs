namespace Jellyfin.Plugin.Anemone.TestKit;

/// <summary>
/// Polls for a condition instead of sleeping a fixed amount - for asserting on the far side of a fire-and-
/// forget continuation (stderr delivery, exit callbacks, background finish/cleanup tasks) without a fixed,
/// arbitrary delay that's either too short (flaky) or too long (slow). Still not a "real" signal (no
/// event to await), but bounded and fails fast with a clear message instead of hanging.
/// </summary>
public static class Waiting
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls <paramref name="condition"/> every 10ms until it's true or <paramref name="timeout"/> elapses.</summary>
    /// <exception cref="TimeoutException">The condition was never true within the timeout.</exception>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"anemone-testkit: condition was not met in time{(because is null ? string.Empty : $" ({because})")}");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
