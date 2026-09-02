# Contributing to jellyfin-anemone

Thanks for looking at this. It's a young project (v0, one homelab) with two codebases in two languages
glued together by [`PROTOCOL.md`](PROTOCOL.md) — read that and [`RESEARCH.md`](RESEARCH.md) before touching
anything, and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for a tour of where things live.

## Prerequisites

- **.NET 9 SDK**, for the plugin. The Homebrew cask (`brew install --cask dotnet-sdk`) needs `sudo`; the
  rootless install avoids that:
  ```sh
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0
  export PATH="$HOME/.dotnet:$PATH"
  ```
- **Rust (stable)**, for the agent. `rustup` is the usual route.
- **A C toolchain, on Linux only** — `cargo build` cannot link the agent without one
  (`sudo apt-get install -y build-essential` on Debian/Ubuntu, or your distro's equivalent). Not needed on
  macOS (Xcode CLT already provides one) or for cross-compiling from macOS.
- **jellyfin-ffmpeg**, to actually run an agent against real media: the official portable builds at
  <https://github.com/jellyfin/jellyfin-ffmpeg/releases> (`macarm64-gpl`, `mac64-gpl`, `linux64-gpl`,
  `linuxarm64-gpl`). Pin the version to whatever the target Jellyfin server ships — a version mismatch
  between server and agent ffmpeg is a routing-refusal condition, not a crash (see `PROTOCOL.md`), but it's
  still confusing to debug against a mismatched build.

## Repo layout

```
plugin/     Jellyfin.Plugin.Anemone (C#, net9.0) + its xunit test project — see plugin/anemone.sln
agent/      polyp (Rust daemon) + anemone-mock (fake plugin for local testing) — see agent/README.md
docs/       upstream-10.11.0/ (verbatim Jellyfin source for rebasing), ARCHITECTURE.md, DEPLOY.md
research/   the research reports (file:line citations) behind RESEARCH.md
scripts/    package / install / deploy helpers
```

## Build & test

### Plugin

```sh
dotnet build plugin/anemone.sln -c Release
dotnet test plugin/anemone.sln
scripts/package-plugin.sh          # → dist/Anemone_<version>/ and dist/Anemone_<version>.zip
```

`scripts/package-plugin.sh` also refuses to build if the output directory would contain anything besides
`Jellyfin.Plugin.Anemone.dll`/`.pdb`/`.deps.json` and `meta.json` — Jellyfin already loads
`AsyncKeyedLock`, `Microsoft.*` and `System.*` itself, and shipping a second copy shadows the host's and
breaks in ways that are miserable to debug. If you add a dependency, make sure it's either something
Jellyfin's own runtime graph already provides (mark it `<ExcludeAssets>runtime</ExcludeAssets>` like
`Jellyfin.Data`/`Jellyfin.Database.Implementations` in the csproj) or you've checked the packaged output
stays clean.

The test project (`plugin/Jellyfin.Plugin.Anemone.Tests`) is organized by area (`Transcoding/`, `Agents/`,
`Ingest/`), including a wire-compat suite (`Agents/WireCompatTests.cs`) that checks the C# and Rust sides of
the protocol agree on framing. Run everything with `dotnet test plugin/anemone.sln`; once tests carry
category traits, filter a single tier with `dotnet test plugin/anemone.sln --filter Category=<name>` — check
the test project for the exact category names in use, they're still settling.

### Agent

```sh
cd agent
cargo build --release
cargo test
cargo clippy --all-targets -- -D warnings
cargo fmt --check
```

`cargo test` runs unit tests against captured ffmpeg fixtures plus `tests/e2e.rs`, which spawns the real
`anemone-mock` and `polyp` binaries against a real local ffmpeg. It skips itself with a clear message (see
it with `cargo test -- --nocapture`) if no ffmpeg is found on `PATH` or at `/opt/homebrew/bin/ffmpeg`. Full
detail, including the mock server's `--once`/`--job-file` modes for scripted runs: `agent/README.md`.

## Running against a real Jellyfin — DryRun first

Never point a freshly built plugin at a production server with routing live. The config page has a
**DryRun** switch for exactly this: it logs every routing decision (`anemone: dry-run — would route …`, or
the reason a job stays local) but always transcodes locally, so playback is byte-for-byte what it would be
without the plugin at all. Bring DryRun on, install, confirm the log lines look right for real playback, and
only then flip it off. `docs/DEPLOY.md` walks through this end to end, including bringing an agent online and
verifying an actual routed transcode.

Rollback at any point: remove the plugin folder from the Jellyfin plugins directory and restart the app —
see the "hard-won rules" below for why "restart" means *quit and relaunch*, not `POST /System/Restart`.

## Hard-won rules — please don't relearn these the hard way

- **Build the plugin against the exact Jellyfin version of the target server.** A version mismatch between
  the `Jellyfin.Controller`/`Jellyfin.Model` NuGet packages and the running server does not fail cleanly —
  it surfaces as the misleading dashboard error "plugin references an incompatible version of one of the
  shared libraries" (really a `ReflectionTypeLoadException` on a specific assembly version). The csproj pins
  an exact version for a reason; when the target server's Jellyfin version changes, bump it there.
- **`POST /System/Restart` does not reload a plugin.** On macOS it's an in-process soft restart, and the CLR
  cannot unload an assembly, so Jellyfin keeps serving the build it loaded first — a freshly built DLL is
  silently ignored and you end up debugging a version that's no longer on disk. Quit and relaunch the app
  instead; `scripts/install-plugin-local.sh` already does this.
- **Keep the `AnemoneTranscodeManager` diff against upstream minimal, and mark every change with a
  `// anemone:` comment.** When Jellyfin releases a new minor, diff the fork against the matching file in
  `docs/upstream-10.11.0/` (replace that directory's contents with the new tag first) and re-fork —
  the smaller the diff, the easier that exercise is next time.
- **The hardware translator must refuse what it doesn't model, never guess.** Every prior distributed-Jellyfin
  project that tried to blindly mangle ffmpeg arguments for hardware it didn't understand broke on exactly
  that (see `PROTOCOL.md`'s "Hardware acceleration" section and `RESEARCH.md` §5). If you're adding a new
  `hwaccel` profile or filter to `HwTranslator`, the safe default on anything unrecognized is "don't route
  this job here," not a best-effort rewrite.
- **Protocol changes go in `PROTOCOL.md` first.** It's the contract between two codebases in two languages —
  update the spec, then both sides, and both sides ship wire-compat tests
  (`plugin/Jellyfin.Plugin.Anemone.Tests/Agents/WireCompatTests.cs` and the Rust `protocol.rs` serde tests).
  A protocol change that only lands in one language's tests isn't done.
- **Commit style: descriptive, imperative, explain *why*.** No AI attribution, no `Co-Authored-By` lines.

## PR checklist

- [ ] Read `PROTOCOL.md` if this touches anything on the wire; updated it first if the wire format changed.
- [ ] `dotnet test plugin/anemone.sln` passes (if `plugin/` changed).
- [ ] `cargo test`, `cargo clippy --all-targets -- -D warnings`, and `cargo fmt --check` pass (if `agent/`
      changed).
- [ ] If the wire protocol changed: both a plugin-side and an agent-side test cover it.
- [ ] If `AnemoneTranscodeManager.cs` changed: every new deviation from upstream is marked `// anemone:`.
- [ ] If a new dependency was added to the plugin: `scripts/package-plugin.sh` still refuses to ship
      anything beyond `Jellyfin.Plugin.Anemone.{dll,pdb,deps.json}` + `meta.json`.
- [ ] Tested against a real Jellyfin server with DryRun on before DryRun off, if this touches routing,
      translation, or ingest.
- [ ] Commit messages are descriptive and imperative, explain *why*, and carry no AI attribution.
