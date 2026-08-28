using System.Runtime.CompilerServices;

// Exposes internal test seams (e.g. AgentHub.PickFrom) to the test project without widening the public API.
[assembly: InternalsVisibleTo("Jellyfin.Plugin.Cluster.Tests")]
