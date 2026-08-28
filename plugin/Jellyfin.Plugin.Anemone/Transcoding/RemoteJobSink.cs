using System.IO.Pipelines;
using System.Text;
using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// anemone: bridges an agent connection's per-job callbacks ("stderr line", "exited") into a
/// <see cref="Pipe"/> whose reader end is fed to Jellyfin's own <c>JobLogger</c> — the same
/// stderr-parsing/progress-reporting code local jobs use. Constructed with no backpressure
/// (<see cref="PipeOptions.PauseWriterThreshold"/> = 0) so <see cref="OnStderrLine"/> never blocks:
/// the interface contract says this is called from the agent connection's socket read loop.
/// </summary>
public sealed class RemoteJobSink : IRemoteJobSink
{
    private readonly PipeWriter _writer;
    private readonly Action<int> _onStarted;
    private readonly Action<int, string?> _onExited;
    private int _exited;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteJobSink"/> class.
    /// </summary>
    /// <param name="writer">The write end of the pipe whose reader is handed to <c>JobLogger</c>.</param>
    /// <param name="onStarted">Invoked (synchronously, cheaply) when the agent reports the job started.</param>
    /// <param name="onExited">Invoked (synchronously, cheaply — heavier work must be dispatched by the callback itself) exactly once, when the job ends.</param>
    public RemoteJobSink(PipeWriter writer, Action<int> onStarted, Action<int, string?> onExited)
    {
        _writer = writer;
        _onStarted = onStarted;
        _onExited = onExited;
    }

    /// <inheritdoc />
    public void OnStarted(int pid) => _onStarted(pid);

    /// <inheritdoc />
    public void OnStderrLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var span = _writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _writer.Advance(bytes.Length);

        // anemone: the pipe has PauseWriterThreshold=0 (no backpressure), so this always completes
        // synchronously - we deliberately don't await it, this method must not block the caller.
        _ = _writer.FlushAsync();
    }

    /// <inheritdoc />
    public void OnExited(int exitCode, string? error)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0)
        {
            return;
        }

        _writer.Complete();
        _onExited(exitCode, error);
    }
}
