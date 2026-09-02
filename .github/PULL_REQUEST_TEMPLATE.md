## What & why

<!-- What this changes, and why — link an issue if there is one. -->

## Checklist

- [ ] Read `PROTOCOL.md` if this touches anything on the wire; updated it first if the wire format changed.
- [ ] `dotnet test plugin/anemone.sln` passes (if `plugin/` changed).
- [ ] `cargo test`, `cargo clippy --all-targets -- -D warnings`, and `cargo fmt --check` pass (if `agent/`
      changed).
- [ ] If the wire protocol changed: both a plugin-side and an agent-side test cover it.
- [ ] If `AnemoneTranscodeManager.cs` changed: every new deviation from upstream is marked `// anemone:`.
- [ ] If a new plugin dependency was added: `scripts/package-plugin.sh` still refuses to ship anything
      beyond `Jellyfin.Plugin.Anemone.{dll,pdb,deps.json}` + `meta.json`.
- [ ] Tested against a real Jellyfin server with DryRun on before DryRun off, if this touches routing,
      translation, or ingest.
- [ ] Commit messages are descriptive and imperative, explain *why*, and carry no AI attribution.

See `CONTRIBUTING.md` for the reasoning behind each of these.

## Testing

<!-- What you actually ran, and against what (mock agent, a real Jellyfin server + real agent, which OS/hwaccel). -->
