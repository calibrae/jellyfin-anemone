using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Cluster.Agents.Protocol;

/// <summary>
/// Base type for every wire frame exchanged with a jfc-agent over the control WebSocket (see PROTOCOL.md).
/// Wire format: one JSON object per text frame, snake_case field names, a <c>type</c> string discriminator.
/// </summary>
public abstract record Frame
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses one JSON text frame. Unknown fields are ignored (System.Text.Json default). A missing or
    /// unrecognized <c>type</c> yields an <see cref="UnknownFrame"/> rather than throwing.
    /// </summary>
    public static Frame Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? string.Empty
            : string.Empty;

        Frame? frame = type switch
        {
            "hello" => root.Deserialize<HelloFrame>(JsonOptions),
            "status" => root.Deserialize<StatusFrame>(JsonOptions),
            "started" => root.Deserialize<StartedFrame>(JsonOptions),
            "stderr" => root.Deserialize<StderrFrame>(JsonOptions),
            "exit" => root.Deserialize<ExitFrame>(JsonOptions),
            "error" => root.Deserialize<ErrorFrame>(JsonOptions),
            "pong" => root.Deserialize<PongFrame>(JsonOptions),
            "welcome" => root.Deserialize<WelcomeFrame>(JsonOptions),
            "reject" => root.Deserialize<RejectFrame>(JsonOptions),
            "job" => root.Deserialize<JobFrame>(JsonOptions),
            "stdin" => root.Deserialize<StdinFrame>(JsonOptions),
            "kill" => root.Deserialize<KillFrame>(JsonOptions),
            "ping" => root.Deserialize<PingFrame>(JsonOptions),
            _ => null,
        };

        return frame ?? new UnknownFrame(type);
    }

    /// <summary>Serializes any frame instance using its runtime type, so derived-record fields are included.</summary>
    public static string Serialize(object frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonSerializer.Serialize(frame, frame.GetType(), JsonOptions);
    }
}
