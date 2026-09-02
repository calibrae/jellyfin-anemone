using System.Runtime.CompilerServices;

// Exposes internal test seams (e.g. AgentHub.PickFrom) to the test projects without widening the public API.
[assembly: InternalsVisibleTo("Jellyfin.Plugin.Anemone.Tests")]
[assembly: InternalsVisibleTo("Jellyfin.Plugin.Anemone.IntegrationTests")]
