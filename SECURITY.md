# Security

## Threat model

Anemone's security model is built for a trusted LAN or point-to-point link (a homelab, a Thunderbolt cable
between two of your own machines) — **not** for exposing agent ports to the internet or to a network you
don't control. Read this before deciding where to put an agent.

- **Control channel** — one WebSocket per agent, authenticated with a single shared secret
  (`SharedSecret` in the plugin config / `secret` in `polyp.toml`), sent as a plain `Authorization: Bearer`
  header. There is no per-agent secret and no rotation mechanism; every agent trusts the same token.
- **Ingest channel** — ffmpeg on the agent PUTs HLS segments back using a per-job, randomly generated
  256-bit bearer token, minted when the job is placed and revoked when it exits. A leaked ingest token is
  bounded to one job's lifetime and can only write files, not read anything.
- **Transport is plain HTTP/WS, by design, not by oversight.** ffmpeg's HLS muxer never checks the TLS
  certificate on its own PUT requests — `-tls_verify` and friends never reach `set_http_options()` for the
  output side (`ffmpeg/libavformat/hlsenc.c`; verified in `RESEARCH.md` §4). A self-signed cert would give
  **zero** protection against a machine-in-the-middle rewriting or exfiltrating video, so there is no benefit
  to wrapping the ingest PUT in TLS with the current ffmpeg-driven design. Plain HTTP on a LAN/Thunderbolt
  link you control is the actual, considered security posture — not a shortcut waiting to be fixed.
- **The agent executes what the server tells it to.** A `job` frame is a full ffmpeg argv chosen by the
  server. `polyp` does not validate or sandbox that argv beyond what `PROTOCOL.md`'s ingest filename rules
  enforce on the output side — **anyone who can reach an agent's control port and knows (or guesses) the
  shared secret can make that agent run arbitrary ffmpeg command lines**, including reading any file ffmpeg
  can open and writing to wherever `-hls_segment_filename`-style output arguments point on the agent's own
  disk within the agent process's permissions. Treat the shared secret with that in mind: it is not "just a
  password," it's equivalent to code execution on every connected agent.

### What this means in practice

- **Do not expose `AgentListenPort` (default 8097) to an untrusted network.** No exceptions for "it's just
  my agent" — see above, the control secret is a code-execution credential.
- Keep agents on a LAN segment or link you control (the reference deployment uses a private Thunderbolt
  subnet and a home LAN). If you must cross an untrusted network, put a VPN (WireGuard, Tailscale, etc.)
  between the server and the agent and treat the tunnel, not ffmpeg's own TLS support, as the trust boundary.
- Generate `SharedSecret` with the dashboard's **Generate** button (or an equivalent random source) rather
  than picking something memorable, and don't commit it — `polyp.toml`'s `secret` field should come from a
  secrets manager or be set out-of-band, same as any other credential.
- Rotating the shared secret currently means updating it in the plugin config and in every agent's
  `polyp.toml` and restarting them; there's no live rotation.

## Reporting a vulnerability

Please **do not open a public issue** for a security report. Email **com@calii.net** with what you found,
how to reproduce it, and its impact. This is a small, one-person-maintained project — expect an
acknowledgment within a few days, not an SLA. Fixes ship as a normal release once confirmed; if a report
turns out to be already covered by the threat model above (e.g. "the ingest PUT isn't authenticated with
TLS"), that's expected behavior, and the reply will say so and point back here.
