using Jellyfin.Plugin.Cluster.Agents;
using Jellyfin.Plugin.Cluster.Agents.Protocol;
using Jellyfin.Plugin.Cluster.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Cluster.Tests.Agents;

public class AgentConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class FakeSink : IRemoteJobSink
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> StartedPids { get; } = [];

        public List<string> StderrLines { get; } = [];

        public (int Code, string? Error)? Exited { get; private set; }

        public void OnStarted(int pid) => StartedPids.Add(pid);

        public void OnStderrLine(string line) => StderrLines.Add(line);

        public void OnExited(int exitCode, string? error)
        {
            Exited = (exitCode, error);
            _exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);
    }

    private static AgentInfo MakeInfo(string name = "trish", int maxSessions = 3) =>
        new(name, "0.1.0", "macos-arm64", "/opt/jfc/ffmpeg", "7.1.2-Jellyfin", [], [], [], [], [], maxSessions, DateTimeOffset.UtcNow);

    private static AgentConnection MakeConnection(FakeAgentWebSocket socket, out CancellationTokenSource runCts, out Task runTask)
    {
        var connection = new AgentConnection(socket, MakeInfo(), 10, NullLogger<AgentConnection>.Instance);
        runCts = new CancellationTokenSource();
        runTask = connection.RunAsync(runCts.Token);
        return connection;
    }

    private static async Task<T> ReadFrameAsync<T>(FakeAgentWebSocket socket)
        where T : Frame
    {
        using var cts = new CancellationTokenSource(Timeout);
        var json = await socket.Outgoing.ReadAsync(cts.Token);
        return Assert.IsType<T>(Frame.Parse(json));
    }

    [Fact]
    public async Task StartJobAsync_ResolvesOnStarted()
    {
        var socket = new FakeAgentWebSocket();
        var connection = MakeConnection(socket, out var runCts, out var runTask);
        var sink = new FakeSink();
        var spec = new RemoteJobSpec("job-1", ["-i", "x"], "tok", "label");

        var startTask = connection.StartJobAsync(spec, sink, CancellationToken.None);

        var jobFrame = await ReadFrameAsync<JobFrame>(socket);
        Assert.Equal("job-1", jobFrame.Id);

        socket.EnqueueIncoming(Frame.Serialize(new StartedFrame("job-1", 4242)));

        var job = await startTask.WaitAsync(Timeout);

        Assert.Equal("job-1", job.Id);
        Assert.Equal("trish", job.AgentName);
        Assert.Equal([4242], sink.StartedPids);

        runCts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task StartJobAsync_FaultsOnExitBeforeStarted()
    {
        var socket = new FakeAgentWebSocket();
        var connection = MakeConnection(socket, out var runCts, out var runTask);
        var sink = new FakeSink();
        var spec = new RemoteJobSpec("job-1", ["-i", "x"], "tok", "label");

        var startTask = connection.StartJobAsync(spec, sink, CancellationToken.None);
        await ReadFrameAsync<JobFrame>(socket);

        socket.EnqueueIncoming(Frame.Serialize(new ExitFrame("job-1", -2, "capacity")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => startTask.WaitAsync(Timeout));
        Assert.Contains("job-1", ex.Message, StringComparison.Ordinal);

        await sink.WaitForExitAsync(default).WaitAsync(Timeout);
        Assert.Equal((-2, "capacity"), sink.Exited);

        runCts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task Stderr_ReachesSink()
    {
        var socket = new FakeAgentWebSocket();
        var connection = MakeConnection(socket, out var runCts, out var runTask);
        var sink = new FakeSink();
        var spec = new RemoteJobSpec("job-1", ["-i", "x"], "tok", "label");

        var startTask = connection.StartJobAsync(spec, sink, CancellationToken.None);
        await ReadFrameAsync<JobFrame>(socket);
        socket.EnqueueIncoming(Frame.Serialize(new StartedFrame("job-1", 1)));
        await startTask.WaitAsync(Timeout);

        const string Line = "frame=  10 fps=30 q=-1.0 size=100KiB time=00:00:01.00 bitrate=800kbits/s speed=1.0x";
        socket.EnqueueIncoming(Frame.Serialize(new StderrFrame("job-1", Line)));

        await WaitUntilAsync(() => sink.StderrLines.Count > 0);
        Assert.Equal(Line, sink.StderrLines[0]);

        runCts.Cancel();
        await Task.WhenAny(runTask, Task.Delay(Timeout));
    }

    [Fact]
    public async Task ConnectionDrop_FiresOnExitedMinusOne()
    {
        var socket = new FakeAgentWebSocket();
        var connection = MakeConnection(socket, out var runCts, out var runTask);
        var sink = new FakeSink();
        var spec = new RemoteJobSpec("job-1", ["-i", "x"], "tok", "label");

        var startTask = connection.StartJobAsync(spec, sink, CancellationToken.None);
        await ReadFrameAsync<JobFrame>(socket);
        socket.EnqueueIncoming(Frame.Serialize(new StartedFrame("job-1", 1)));
        var job = await startTask.WaitAsync(Timeout);

        // Simulate the agent's connection dropping.
        socket.CompleteIncoming();

        await runTask.WaitAsync(Timeout);

        Assert.Equal((-1, "connection lost"), sink.Exited);
        Assert.Equal(-1, await job.Completion.WaitAsync(Timeout));
        Assert.False(connection.IsConnected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not met in time");
            }

            await Task.Delay(10);
        }
    }
}
