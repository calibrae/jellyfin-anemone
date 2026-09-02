namespace Jellyfin.Plugin.Anemone.Transcoding;

/// <summary>
/// The pure decision behind <see cref="Configuration.PluginConfiguration.PreferRemote"/>/
/// <see cref="Configuration.PluginConfiguration.LocalMaxSessions"/> - kept separate from
/// <see cref="AnemoneTranscodeManager"/> so the policy is unit-testable without a live manager, and
/// separate from <see cref="IJobRouter"/> so the router itself stays a pure function of "can this job go
/// remote", not "should it, given what else is running locally right now".
/// </summary>
public static class RemotePlacementPolicy
{
    /// <summary>
    /// True when a new HLS job should even be offered to <see cref="IJobRouter"/>.
    /// </summary>
    /// <param name="preferRemote">
    /// <see cref="Configuration.PluginConfiguration.PreferRemote"/>. When true, always returns true -
    /// today's default behaviour: route to an agent whenever one qualifies.
    /// </param>
    /// <param name="localMaxSessions">
    /// <see cref="Configuration.PluginConfiguration.LocalMaxSessions"/> - how many concurrent local
    /// transcodes the server keeps for itself before it starts offering new jobs to agents. Only
    /// consulted when <paramref name="preferRemote"/> is false.
    /// </param>
    /// <param name="activeLocalJobs">
    /// How many local (non-agent) transcodes are running right now, not counting the job about to start.
    /// </param>
    /// <returns>
    /// False (stay local, never even ask the router) while <paramref name="activeLocalJobs"/> is below
    /// <paramref name="localMaxSessions"/>; true (consult the router) once it reaches or exceeds the cap,
    /// or whenever <paramref name="preferRemote"/> is true. A <paramref name="localMaxSessions"/> of 0
    /// therefore always returns true - "keep zero jobs local" is a valid way to say "always prefer remote"
    /// without flipping <paramref name="preferRemote"/>.
    /// </returns>
    public static bool ShouldConsultRouter(bool preferRemote, int localMaxSessions, int activeLocalJobs)
        => preferRemote || activeLocalJobs >= localMaxSessions;
}
