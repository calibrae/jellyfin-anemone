---
name: Bug report
about: Something didn't work — a job failed to route, a translation was wrong, an agent misbehaved, etc.
title: ""
labels: bug
assignees: ""
---

## What happened

<!-- What you expected, what actually happened. -->

## Environment

- Jellyfin version:
- Anemone plugin version (`meta.json` / dashboard → Plugins → Anemone):
- Agent OS / hardware / hwaccel (e.g. "Debian 13, Intel Iris Xe, vaapi"):
- Agent `polyp` version:

## Does it reproduce with hardware-profile translation off?

Dashboard → Plugins → Anemone → **Allow hardware-profile translation** = off, then reproduce again.

- [ ] Still happens with translation off
- [ ] Only happens with translation on
- [ ] Didn't test this

## Logs

### `anemone:` lines from the Jellyfin server log

<!--
On macOS: ~/Library/Application Support/jellyfin/log/log_<date>.log
grep -i anemone <log file> around the time of the failure, including the routing decision
("anemone: routed to agent ...", "anemone: dry-run — would route ...", or the "not routing" reason).
-->

```
paste here
```

### The ffmpeg command line from `FFmpeg.Transcode-*.log`

<!--
Jellyfin's transcode log directory (dashboard → Logs, or the transcodes cache dir). Paste the
command line the failing job ran with — local or remote, whichever applies.
-->

```
paste here
```

### Agent log (`polyp`), if the job reached an agent

<!-- macOS: /var/log/polyp/polyp.log + polyp.err.log. Linux: journalctl -u polyp -->

```
paste here
```

## Anything else

<!-- Media file details if relevant (codec, HDR/10-bit, container), whether this is new or a regression, etc. -->
