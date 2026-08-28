using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Cluster.Tests.Agents;

/// <summary>
/// In-memory <see cref="WebSocket"/> standing in for a jfc-agent's end of the connection. Tests push
/// incoming text frames with <see cref="EnqueueIncoming"/> (simulating the agent) and read what the server
/// sent back from <see cref="Outgoing"/>. <see cref="CompleteIncoming"/> simulates the agent disconnecting.
/// </summary>
internal sealed class FakeAgentWebSocket : WebSocket
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();

    private byte[]? _pending;
    private int _pendingOffset;
    private WebSocketState _state = WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override string? SubProtocol => null;

    public override WebSocketState State => _state;

    /// <summary>Frames the server sent, in order. Read with <c>await Outgoing.ReadAsync(...)</c>.</summary>
    public ChannelReader<string> Outgoing => _outgoing.Reader;

    /// <summary>Queues a text frame as if the agent sent it.</summary>
    public void EnqueueIncoming(string json) => _incoming.Writer.TryWrite(json);

    /// <summary>Simulates the agent disconnecting: the next <see cref="ReceiveAsync"/> returns a Close result.</summary>
    public void CompleteIncoming() => _incoming.Writer.TryComplete();

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            string json;
            try
            {
                json = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
            }

            _pending = Encoding.UTF8.GetBytes(json);
            _pendingOffset = 0;
        }

        var remaining = _pending.Length - _pendingOffset;
        var toCopy = Math.Min(remaining, buffer.Count);
        Array.Copy(_pending, _pendingOffset, buffer.Array!, buffer.Offset, toCopy);
        _pendingOffset += toCopy;

        var endOfMessage = _pendingOffset >= _pending.Length;
        if (endOfMessage)
        {
            _pending = null;
            _pendingOffset = 0;
        }

        return new WebSocketReceiveResult(toCopy, WebSocketMessageType.Text, endOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (messageType == WebSocketMessageType.Text && endOfMessage)
        {
            var json = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            _outgoing.Writer.TryWrite(json);
        }

        return Task.CompletedTask;
    }
}
