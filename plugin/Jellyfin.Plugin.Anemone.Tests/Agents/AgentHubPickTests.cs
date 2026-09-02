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

    private static IAgentConnection? PickBest(IEnumerable<IAgentConnection> agents, JobRequirements requirements, DateTimeOffset now, TimeSpan deadAfter, bool requireMatchingFfmpeg, string? serverFfmpegVersion)
        => AgentHub.CandidatesFrom(agents, requirements, now, deadAfter, requireMatchingFfmpeg, serverFfmpegVersion).FirstOrDefault();

    [Fact]
    public void Candidates_ReturnsEmpty_WhenNoAgents()
    {
        var candidates = AgentHub.CandidatesFrom([], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_ExcludesAgentAtCapacity()
    {
        var full = MakeAgent("full", maxSessions: 2, activeJobs: 2);

        var candidates = AgentHub.CandidatesFrom([full], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_ExcludesDeadAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var dead = MakeAgent("dead", lastSeen: now - TimeSpan.FromMinutes(5));

        var candidates = AgentHub.CandidatesFrom([dead], NoRequirements, now, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_ExcludesDisconnectedAgent()
    {
        var disconnected = MakeAgent("gone", isConnected: false);

        var candidates = AgentHub.CandidatesFrom([disconnected], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_DoesNotExcludeAgentMissingHwaccel()
    {
        // Placement no longer filters on hwaccel/encoders/decoders/filters - a different-hardware agent
        // is still a candidate, because JobRouter + HwTranslator may be able to translate the job for it.
        // (Before protocol v2 this agent would have been excluded here; now that's HwTranslator's call.)
        var agent = MakeAgent("no-vt", hwaccels: []);
        var requirements = new JobRequirements(["videotoolbox"], [], [], [], []);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_DoesNotExcludeAgentMissingEncoders()
    {
        var agent = MakeAgent("no-h264-vt", encoders: []);
        var requirements = new JobRequirements([], ["h264_videotoolbox"], [], [], []);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_ExcludesAgent_WhenInputPathNotUnderAnOkMount()
    {
        // /Volumes/data covers /Volumes/data/x.mkv but must NOT cover /Volumes/database/x.mkv (prefix must
        // land on a directory boundary, not just a string prefix).
        var agent = MakeAgent("has-data-mount", mounts: [new AgentMount("/Volumes/data", true)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/database/x.mkv"]);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_IncludesAgent_WhenInputPathIsUnderMount()
    {
        var agent = MakeAgent("has-data-mount", mounts: [new AgentMount("/Volumes/data", true)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/data/x.mkv"]);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_ExcludesAgent_WhenMountIsNotOk()
    {
        var agent = MakeAgent("broken-mount", mounts: [new AgentMount("/Volumes/data", false)]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/data/x.mkv"]);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_IncludesAgent_WhenInputPathIsUnderMountsServerPath()
    {
        // server_path is what the SERVER calls the tree; path is where the agent has it mounted. Coverage
        // must be checked against server_path, not path.
        var agent = MakeAgent("mapped-mount", mounts: [new AgentMount("/mnt/media", true, "/Volumes/data")]);
        var requirements = new JobRequirements([], [], [], [], ["/Volumes/data/x.mkv"]);

        var candidates = AgentHub.CandidatesFrom([agent], requirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_ExcludesAgent_OnFfmpegMajorMinorMismatch()
    {
        var agent = MakeAgent("old-ffmpeg", ffmpegVersion: "7.0.1-Jellyfin");

        var candidates = AgentHub.CandidatesFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, "7.1.2-Jellyfin");

        Assert.Empty(candidates);
    }

    [Fact]
    public void Candidates_IncludesAgent_OnFfmpegMajorMinorMatch()
    {
        var agent = MakeAgent("same-ffmpeg", ffmpegVersion: "7.1.9-Jellyfin");

        var candidates = AgentHub.CandidatesFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, "7.1.2-Jellyfin");

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_SkipsFfmpegCheck_WhenServerVersionUnknown()
    {
        var agent = MakeAgent("whatever-ffmpeg", ffmpegVersion: "9.9.9");

        var candidates = AgentHub.CandidatesFrom([agent], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), true, null);

        Assert.Same(agent, Assert.Single(candidates));
    }

    [Fact]
    public void Candidates_OrdersLeastLoadedByRatioFirst()
    {
        var moreLoadedByRatio = MakeAgent("b", maxSessions: 2, activeJobs: 1); // ratio 0.5
        var lessLoadedByRatio = MakeAgent("a", maxSessions: 4, activeJobs: 1); // ratio 0.25

        var candidates = AgentHub.CandidatesFrom(
            [moreLoadedByRatio, lessLoadedByRatio],
            NoRequirements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            false,
            null);

        Assert.Equal([lessLoadedByRatio, moreLoadedByRatio], candidates);
    }

    [Fact]
    public void Candidates_TieBreaksOnLowerActiveJobCount()
    {
        var moreActive = MakeAgent("more", maxSessions: 4, activeJobs: 2); // ratio 0.5
        var lessActive = MakeAgent("less", maxSessions: 2, activeJobs: 1); // ratio 0.5

        var candidates = AgentHub.CandidatesFrom(
            [moreActive, lessActive],
            NoRequirements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            false,
            null);

        Assert.Equal([lessActive, moreActive], candidates);
    }

    [Fact]
    public void Candidates_TieBreaksOnMostRecentlySeen()
    {
        var now = DateTimeOffset.UtcNow;
        var older = MakeAgent("older", maxSessions: 2, activeJobs: 1, lastSeen: now - TimeSpan.FromSeconds(20));
        var newer = MakeAgent("newer", maxSessions: 2, activeJobs: 1, lastSeen: now - TimeSpan.FromSeconds(1));

        var candidates = AgentHub.CandidatesFrom([older, newer], NoRequirements, now, TimeSpan.FromSeconds(30), false, null);

        Assert.Equal([newer, older], candidates);
    }

    [Fact]
    public void Candidates_PicksBestFirst_ConvenienceHelperMatchesFirstOfList()
    {
        var worse = MakeAgent("worse", maxSessions: 2, activeJobs: 1); // ratio 0.5
        var better = MakeAgent("better", maxSessions: 4, activeJobs: 1); // ratio 0.25

        var picked = PickBest([worse, better], NoRequirements, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), false, null);

        Assert.Same(better, picked);
    }
}
