using Jellyfin.Plugin.Cluster.Transcoding;

namespace Jellyfin.Plugin.Cluster.Tests.Transcoding;

public class KeyedLockTests
{
    [Fact]
    public async Task LockAsync_SameKey_Serializes()
    {
        using var keyedLock = new KeyedLock();
        var order = new List<int>();
        var enteredFirst = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            using var handle = await keyedLock.LockAsync("a");
            order.Add(1);
            enteredFirst.SetResult();

            // Hold the lock briefly so the second attempt has to actually wait.
            await Task.Delay(100);
            order.Add(2);
        });

        await enteredFirst.Task;

        var second = Task.Run(async () =>
        {
            using var handle = await keyedLock.LockAsync("a");
            order.Add(3);
        });

        await Task.WhenAll(first, second);

        // The second acquirer must not observe the lock until the first has released it: 1 (entered),
        // 2 (first releases), 3 (second finally gets in) - never 1, 3, 2.
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task LockAsync_DifferentKeys_DoNotSerialize()
    {
        using var keyedLock = new KeyedLock();
        var aEntered = new TaskCompletionSource();
        var bEntered = new TaskCompletionSource();

        var a = Task.Run(async () =>
        {
            using var handle = await keyedLock.LockAsync("a");
            aEntered.SetResult();

            // If "b" were serialized behind "a", this delay would make the test time out waiting for
            // bEntered before "a" releases.
            await Task.Delay(200);
        });

        var b = Task.Run(async () =>
        {
            using var handle = await keyedLock.LockAsync("b");
            bEntered.SetResult();
        });

        var completed = await Task.WhenAny(bEntered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(bEntered.Task, completed);

        await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task LockAsync_ReleasedLock_CanBeReacquired()
    {
        using var keyedLock = new KeyedLock();

        (await keyedLock.LockAsync("a")).Dispose();
        (await keyedLock.LockAsync("a")).Dispose();

        // No deadlock / exception means the key's semaphore was properly released and re-rented both times.
        using var handle = await keyedLock.LockAsync("a");
    }

    [Fact]
    public async Task LockAsync_ManyConcurrentAcquirersOnSameKey_OnlyOneInsideAtATime()
    {
        using var keyedLock = new KeyedLock();
        var concurrentCount = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using var handle = await keyedLock.LockAsync("shared");

            lock (gate)
            {
                concurrentCount++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCount);
            }

            await Task.Delay(5);

            lock (gate)
            {
                concurrentCount--;
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxObservedConcurrency);
    }
}
