using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Jellyfin.Plugin.Anemone.Agents.Protocol;
using Jellyfin.Plugin.Anemone.Contracts;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Anemone.Agents;

/// <summary>
/// Wraps one accepted <see cref="WebSocket"/> to a connected polyp, past the hello/welcome handshake
/// (that part is handled by <see cref="AgentHub"/>). Owns a single writer (a channel drained by one send
/// task) and a read loop that dispatches frames to pending jobs. See PROTOCOL.md.
/// </summary>
public sealed class AgentConnection : IAgentConnection
{
    private readonly WebSocket _socket;
    private readonly int _pingIntervalSeconds;
    private readonly ILogger<AgentConnection> _logger;
    private readonly Channel<string> _sendChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly ConcurrentDictionary<string, PendingJob> _pendingJobs = new();
    private readonly SpeedTracker _speedTracker = new();

    private AgentInfo _info;
    private CancellationTokenSource? _runCts;

    public AgentConnection(WebSocket socket, AgentInfo info, string ingestBase, int pingIntervalSeconds, ILogger<AgentConnection> logger)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        IngestBase = ingestBase;
        _pingIntervalSeconds = pingIntervalSeconds;
        _logger = logger;
        LastSeen = DateTimeOffset.UtcNow;
    }

    public AgentInfo Info => _info;

    /// <inheritdoc />
    public string IngestBase { get; }

    public int ActiveJobs { get; private set; }

    public bool IsConnected { get; private set; }

    public DateTimeOffset LastSeen { get; private set; }

    /// <inheritdoc />
    public double? MeasuredSpeed => _speedTracker.Average;

    /// <inheritdoc />
    public double? Load { get; private set; }

    /// <summary>Reads one complete text message from a WebSocket (handling fragmentation). Null = socket closed.</summary>
    internal static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        MemoryStream? textBuffer = null;

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                textBuffer ??= new MemoryStream();
                textBuffer.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                if (textBuffer is null)
                {
                    // Completed a non-text message we didn't buffer (protocol only uses text frames); wait for the next one.
                    continue;
                }

                return Encoding.UTF8.GetString(textBuffer.ToArray());
            }
        }
    }

    /// <summary>
    /// Runs the read/write/ping loops until the socket closes or <paramref name="hostCancellationToken"/> fires.
    /// On return every pending job has been failed and <see cref="IsConnected"/> is false.
    /// </summary>
    internal async Task RunAsync(CancellationToken hostCancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        var token = _runCts.Token;
        IsConnected = true;

        var writerTask = WriterLoopAsync(token);
        var pingTask = PingLoopAsync(token);

        try
        {
            await ReadLoopAsync(token).ConfigureAwait(false);
        }
        finally
        {
            IsConnected = false;
            _runCts.Cancel();
            _sendChannel.Writer.TryComplete();

            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "anemone: agent {Name} writer loop faulted", _info.Name);
            }

            try
            {
                await pingTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "anemone: agent {Name} ping loop faulted", _info.Name);
            }

            FailAllPendingJobs();

            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>Forces the connection closed (duplicate hello replacement, dead-agent reaper, server shutdown).</summary>
    internal async Task CloseAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed by RunAsync's completion; nothing to cancel
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken).ConfigureAwait(false);
            }
            else if (_socket.State is not WebSocketState.Closed and not WebSocketState.Aborted)
            {
                _socket.Abort();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "anemone: error closing agent {Name} connection ({Reason})", _info.Name, reason);
        }
    }

    public async Task<IRemoteJob> StartJobAsync(RemoteJobSpec spec, IRemoteJobSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sink);

        var job = new RemoteJob(spec.Id, _info.Name, this);
        var startAck = new TaskCompletionSource<IRemoteJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingJob(job, sink, startAck);

        if (!_pendingJobs.TryAdd(spec.Id, entry))
        {
            throw new InvalidOperationException($"anemone: duplicate job id {spec.Id} on agent {_info.Name}");
        }

        EnqueueSend(new JobFrame(spec.Id, spec.Argv, spec.IngestToken, spec.Label, spec.Environment));

        var timeoutSeconds = Math.Max(1, Plugin.Instance?.Configuration.AgentStartTimeoutSeconds ?? 15);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var registration = linked.Token.Register(() => startAck.TrySetCanceled(linked.Token));

        try
        {
            return await startAck.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _pendingJobs.TryRemove(spec.Id, out _);
            TryEnqueueKill(spec.Id);
            throw new TimeoutException(
                $"anemone: agent {_info.Name} did not confirm start of job {spec.Id} within {timeoutSeconds}s");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingJobs.TryRemove(spec.Id, out _);
            TryEnqueueKill(spec.Id);
            throw;
        }
    }

    internal Task SendStdinAsync(string id, string data, CancellationToken cancellationToken)
    {
        EnqueueSend(new StdinFrame(id, data));
        return Task.CompletedTask;
    }

    internal Task SendKillAsync(string id, CancellationToken cancellationToken)
    {
        EnqueueSend(new KillFrame(id));
        return Task.CompletedTask;
    }

    private void TryEnqueueKill(string id)
    {
        try
        {
            EnqueueSend(new KillFrame(id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "anemone: failed to send best-effort kill for job {Id} on agent {Name}", id, _info.Name);
        }
    }

    private bool EnqueueSend(Frame frame) => _sendChannel.Writer.TryWrite(Frame.Serialize(frame));

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var json in _sendChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "anemone: agent {Name} writer loop ended", _info.Name);
        }
    }

    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _pingIntervalSeconds)));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow - LastSeen >= TimeSpan.FromSeconds(_pingIntervalSeconds))
                {
                    EnqueueSend(new PingFrame());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? json;
            try
            {
                json = await ReceiveTextMessageAsync(_socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (json is null)
            {
                break;
            }

            LastSeen = DateTimeOffset.UtcNow;

            Frame frame;
            try
            {
                frame = Frame.Parse(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "anemone: agent {Name} sent malformed frame: {Json}", _info.Name, json);
                continue;
            }

            Dispatch(frame);
        }
    }

    private void Dispatch(Frame frame)
    {
        switch (frame)
        {
            case StatusFrame status:
                HandleStatus(status);
                break;
            case StartedFrame started:
                HandleStarted(started);
                break;
            case StderrFrame stderrLine:
                HandleStderr(stderrLine);
                break;
            case ExitFrame exit:
                HandleExit(exit);
                break;
            case ErrorFrame error:
                HandleError(error);
                break;
            case PingFrame:
                EnqueueSend(new PongFrame());
                break;
            case PongFrame:
                // LastSeen already updated above
                break;
            case UnknownFrame unknown:
                _logger.LogDebug("anemone: agent {Name} sent unknown frame type '{Type}'", _info.Name, unknown.Type);
                break;
            default:
                _logger.LogDebug("anemone: agent {Name} sent unexpected frame {Type}", _info.Name, frame.GetType().Name);
                break;
        }
    }

    private void HandleStatus(StatusFrame status)
    {
        ActiveJobs = status.Active;
        if (status.Load is { } load)
        {
            Load = load;
        }

        if (status.Mounts is { Count: > 0 })
        {
            _info = _info with { Mounts = status.Mounts.Select(m => new AgentMount(m.Path, m.Ok, m.ServerPath, m.Local)).ToList() };
        }
    }

    private void HandleStarted(StartedFrame started)
    {
        if (_pendingJobs.TryGetValue(started.Id, out var entry))
        {
            entry.Sink.OnStarted(started.Pid);
            entry.StartAck.TrySetResult(entry.Job);
        }
        else
        {
            _logger.LogDebug("anemone: agent {Name} 'started' for unknown job {Id}", _info.Name, started.Id);
        }
    }

    private void HandleStderr(StderrFrame line)
    {
        if (_pendingJobs.TryGetValue(line.Id, out var entry))
        {
            entry.Sink.OnStderrLine(line.Line);
        }

        // anemone: throughput measurement piggybacks on the same stderr lines Jellyfin's progress parser
        // already consumes (see PROTOCOL.md "Placement inputs (v2.1)") - tracked per connection, not per
        // job, so it averages across every job this agent has run since it (re)connected.
        if (SpeedTracker.TryParseSpeed(line.Line, out var speed))
        {
            _speedTracker.Observe(speed);
        }
    }

    private void HandleExit(ExitFrame exit)
    {
        if (!_pendingJobs.TryRemove(exit.Id, out var entry))
        {
            _logger.LogDebug("anemone: agent {Name} 'exit' for unknown job {Id}", _info.Name, exit.Id);
            return;
        }

        entry.Sink.OnExited(exit.Code, exit.Error);
        entry.Job.CompleteExited(exit.Code);

        if (!entry.StartAck.Task.IsCompleted)
        {
            var message = string.IsNullOrEmpty(exit.Error)
                ? $"anemone: job {exit.Id} exited (code {exit.Code}) before agent confirmed start"
                : $"anemone: job {exit.Id} exited (code {exit.Code}) before agent confirmed start: {exit.Error}";
            entry.StartAck.TrySetException(new InvalidOperationException(message));
        }
    }

    private void HandleError(ErrorFrame error)
    {
        if (!string.IsNullOrEmpty(error.Id))
        {
            _logger.LogWarning("anemone: agent {Name} job {Id} error: {Message}", _info.Name, error.Id, error.Message);
        }
        else
        {
            _logger.LogWarning("anemone: agent {Name} error: {Message}", _info.Name, error.Message);
        }
    }

    private void FailAllPendingJobs()
    {
        foreach (var id in _pendingJobs.Keys.ToList())
        {
            if (!_pendingJobs.TryRemove(id, out var entry))
            {
                continue;
            }

            entry.Sink.OnExited(-1, "connection lost");
            entry.Job.CompleteExited(-1);
            entry.StartAck.TrySetException(
                new InvalidOperationException($"anemone: agent {_info.Name} connection lost while starting job {id}"));
        }
    }

    private sealed record PendingJob(RemoteJob Job, IRemoteJobSink Sink, TaskCompletionSource<IRemoteJob> StartAck);

    private sealed class RemoteJob : IRemoteJob
    {
        private readonly AgentConnection _connection;
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemoteJob(string id, string agentName, AgentConnection connection)
        {
            Id = id;
            AgentName = agentName;
            _connection = connection;
        }

        public string Id { get; }

        public string AgentName { get; }

        public Task<int> Completion => _completion.Task;

        public Task SendStdinAsync(string data, CancellationToken cancellationToken = default) =>
            _connection.SendStdinAsync(Id, data, cancellationToken);

        public Task KillAsync(CancellationToken cancellationToken = default) =>
            _connection.SendKillAsync(Id, cancellationToken);

        internal void CompleteExited(int exitCode) => _completion.TrySetResult(exitCode);
    }
}
