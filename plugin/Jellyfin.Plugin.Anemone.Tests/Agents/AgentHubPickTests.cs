using Jellyfin.Plugin.Anemone.Agents;
using Jellyfin.Plugin.Anemone.Contracts;

namespace Jellyfin.Plugin.Anemone.Tests.Agents;

public class AgentHubPickTests
{
    private static readonly JobRequirements NoRequirements = new([], [], [], [], []);

    private sealed class FakeAgentConnection : IAgentConnection
    {
        public required AgentInfo Info { get; init; }

        public int ActiveJobs { get; init; }

        public bool IsConnected { get; init; } = true;

        public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

        public Task<IRemoteJob> StartJobAsync(RemoteJobSpec spec, IRemoteJobSink sink, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not needed for placement tests");
    }

    private static AgentInfo MakeInfo(
        string name,
        int maxSessions,
        IReadOnlyList<string>? hwaccels,
        IReadOnlyList<string>? encoders,
        IReadOnlyList<AgentMount>? mounts,
        string ffmpegVersion)
    {
        return new AgentInfo(
            name,
            "0.1.0",
            "macos-arm64",
            "/opt/anemone/ffmpeg",
            ffmpegVersion,
            hwaccels ?? [],
            encoders ?? [],
            [],
            [],
            mounts ?? [new AgentMount("/Volumes/data", true)],
            maxSessions,
            DateTimeOffset.UtcNow);
    }

    private static FakeAgentConnection MakeAgent(
        string name,
        int maxSessions = 3,
        int activeJobs = 0,
        bool isConnected = true,
        DateTimeOffset? lastSeen = null,
        IReadOnlyList<string>? hwaccels = null,
        IReadOnlyList<string>? encoders = null,
        IReadOnlyList<AgentMount>? mounts = null,
        string ffmpegVersion = "7.1.2-Jellyfin")
    {
        return new FakeAgentConnection
        {
            Info = MakeInfo(name, maxSessions, hwaccels, encoders, mounts, ffmpegVersion),
            ActiveJobs = activeJobs,
            IsConnected = isConnected,
            LastSeen = lastSeen ?? DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public void Pick_ReturnsNull_WhenNoAgents()
    {
        var picked = AgentHub.PickFrom([], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesAgentAtCapacity()
    {
        var full = MakeAgent("full", maxSessions: 2, activeJobs: 2);

        var picked = AgentHub.PickFrom([full], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesDeadAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var dead = MakeAgent("dead", lastSeen: now - TimeSpan.FromMinutes(5));

        var picked = AgentHub.PickFrom([dead], NoRequirements, now, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesDisconnectedAgent()
    {
        var disconnected = MakeAgent("gone", isConnected: false);

        var picked = AgentHub.PickFrom([disconnected], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesAgentMissingHwaccel()
    {
        var agent = MakeAgent("no-vt", hwaccels: []);
        var requirements = new JobRequirements(["videotoolbox"], [], [], [], []);

        var picked = AgentHub.PickFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_IncludesAgentWithRequiredHwaccel_CaseInsensitive()
    {
        var agent = MakeAgent("vt", hwaccels: ["VideoToolbox"]);
        var requirements = new JobRequirements(["videotoolbox"], [], [], [], []);

        var picked = AgentHub.PickFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, picked);
    }

    [Fact]
    public void Pick_ExcludesAgent_WhenInputPathNotUnderAnOkMount()
    {
        // /Volumes/data covers /Volumes/data/x.mkv but must NOT cover /Volumes/database/x.mkv (prefix must
        // land on a directory boundary, not just a string prefix).
        var agent = MakeAgent("has-data-mount", mounts: [new AgentMount("/Volumes/data", true)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/database/x.mkv"]);

        var picked = AgentHub.PickFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_IncludesAgent_WhenInputPathIsUnderMount()
    {
        var agent = MakeAgent("has-data-mount", mounts: [new AgentMount("/Volumes/data", true)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/data/x.mkv"]);

        var picked = AgentHub.PickFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, picked);
    }

    [Fact]
    public void Pick_ExcludesAgent_WhenMountIsNotOk()
    {
        var agent = MakeAgent("broken-mount", mounts: [new AgentMount("/Volumes/data", false)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/data/x.mkv"]);

        var picked = AgentHub.PickFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExcludesAgent_OnFfmpegMajorMinorMismatch()
    {
        var agent = MakeAgent("old-ffmpeg", ffmpegVersion: "7.0.1-Jellyfin");

        var picked = AgentHub.PickFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, "7.1.2-Jellyfin");

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_IncludesAgent_OnFfmpegMajorMinorMatch()
    {
        var agent = MakeAgent("same-ffmpeg", ffmpegVersion: "7.1.9-Jellyfin");

        var picked = AgentHub.PickFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, "7.1.2-Jellyfin");

        Assert.Same(agent, picked);
    }

    [Fact]
    public void Pick_SkipsFfmpegCheck_WhenServerVersionUnknown()
    {
        var agent = MakeAgent("whatever-ffmpeg", ffmpegVersion: "9.9.9");

        var picked = AgentHub.PickFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, null);

        Assert.Same(agent, picked);
    }

    [Fact]
    public void Pick_ChoosesLeastLoadedByRatio()
    {
        var moreLoadedByRatio = MakeAgent("b", maxSessions: 2, activeJobs: 1); // ratio 0.5
        var lessLoadedByRatio = MakeAgent("a", maxSessions: 4, activeJobs: 1); // ratio 0.25

        var picked = AgentHub.PickFrom(
            [moreLoadedByRatio, lessLoadedByRatio],
            NoRequirements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            false,
            null);

        Assert.Same(lessLoadedByRatio, picked);
    }

    [Fact]
    public void Pick_TieBreaksOnLowerActiveJobCount()
    {
        var moreActive = MakeAgent("more", maxSessions: 4, activeJobs: 2); // ratio 0.5
        var lessActive = MakeAgent("less", maxSessions: 2, activeJobs: 1); // ratio 0.5

        var picked = AgentHub.PickFrom(
            [moreActive, lessActive],
            NoRequirements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            false,
            null);

        Assert.Same(lessActive, picked);
    }

    [Fact]
    public void Pick_TieBreaksOnMostRecentlySeen()
    {
        var now = DateTimeOffset.UtcNow;
        var older = MakeAgent("older", maxSessions: 2, activeJobs: 1, lastSeen: now - TimeSpan.FromSeconds(20));
        var newer = MakeAgent("newer", maxSessions: 2, activeJobs: 1, lastSeen: now - TimeSpan.FromSeconds(1));

        var picked = AgentHub.PickFrom([older, newer], NoRequirements, now, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(newer, picked);
    }
}
