namespace Jellyfin.Plugin.Anemone.Agents.Protocol;

/// <summary>A frame whose <c>type</c> discriminator is missing or not recognized. Logged and ignored by callers.</summary>
public sealed record UnknownFrame(string Type) : Frame;
