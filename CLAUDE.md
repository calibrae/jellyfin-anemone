# jellyfincluster

Jellyfin plugin + Rust agent that offloads live ffmpeg transcodes to other machines; ffmpeg on the agent pushes HLS
segments back over HTTP PUT. Read `RESEARCH.md` (why) and `PROTOCOL.md` (what) before touching anything.

- `plugin/` — C# net9.0, `Jellyfin.Controller` 10.11.x. `ClusterTranscodeManager` is a fork of upstream
  `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` @ v10.11.0 — keep the diff against upstream minimal
  and marked with `// jfc:` comments so the next Jellyfin minor can be re-based.
- `agent/` — Rust, `jfc-agent` (daemon) and `jfc-mock-server` (fake plugin for local testing).
- Target server: speedwagon, Jellyfin 10.11.0, macOS, VideoToolbox. First agent: trish over the TB link (10.240.0.2).
- Never ship a second copy of an assembly Jellyfin already loads (AsyncKeyedLock, Microsoft.*, System.*): plugin
  output must contain only `Jellyfin.Plugin.Cluster.dll` + `meta.json`.
- Build: `dotnet build plugin/Jellyfin.Plugin.Cluster -c Release`, `cargo build --release` in `agent/`.
- Tests: `dotnet test plugin/Jellyfin.Plugin.Cluster.Tests`, `cargo test` in `agent/`.
