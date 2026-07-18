# Jellyfin TVHeadend Plugin Fork

This repository is a fork of the official
[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend)
plugin. It keeps the original TVHeadend plugin identity and adds local work aimed
at newer Jellyfin builds, more reliable HTSP playback, and better diagnostics.

Do not install this side-by-side with the upstream plugin. It is the same
Jellyfin plugin, with fork-specific changes.

## What It Includes

- Jellyfin Live TV backed by TVHeadend channels, EPG data, timers, series
  timers, and recordings.
- TVHeadend recording management from Jellyfin, including DVR profiles,
  priorities, pre/post padding, and an optional synthetic "TVHeadend Recordings"
  channel.
- Streaming through HTSP, HTTP ticket URLs, or HTTP basic authentication.
- HTSP direct streaming with shared upstream subscriptions, independent buffered
  readers, clean-keyframe startup, optional initial tune buffering, and a silent
  stream watchdog.
- An in-plugin MPEG-TS muxer for HTSP payloads, including common video, audio,
  DVB subtitle, teletext, and private/fallback stream handling.
- Signal monitoring and recovery for HTSP streams, including lock/SNR/UNC
  tracking, damaged video withholding until a clean keyframe, and bounded
  reconnects.
- Runtime status in the plugin settings page: connection state, active tuners,
  reader counts, signal metrics, queue health, drops, reconnects, startup cache
  state, and per-stream packet/event counters.
- Jellyfin 10.11 / .NET 9 packaging metadata.

## Requirements

- Jellyfin server compatible with plugin ABI `10.11.0.0`.
- TVHeadend with HTTP and HTSP access enabled.
- .NET 9 SDK to build from source.

## Installation

Use the normal Jellyfin plugin installation flow when installing a packaged
release:

[Jellyfin plugin installation documentation](https://jellyfin.org/docs/general/server/plugins/index.html#installing)

For a manual local build:

```powershell
dotnet publish --configuration Release --output bin
```

Then copy the built `TVHeadEnd.dll` into Jellyfin's `plugins/tvheadend` folder
and restart Jellyfin.

## Configuration Notes

The settings page lets you configure the TVHeadend host, HTTP/HTSP ports, HTTPS,
web root, credentials, timezone, streaming method, recording profile, and HTSP
reliability options.

HTSP is the default streaming method in this fork. HTTP ticket/basic streaming is
still available when you want TVHeadend to provide the transport stream directly.

## Building and Releasing

The project targets `net9.0` and can be built with:

```powershell
dotnet build
```

Packaged releases can be produced with
[Jellyfin Plugin Repository Manager](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
using the included `build.yaml`.

## Upstream

This fork is based on the Jellyfin TVHeadend plugin. For upstream issues,
documentation, and contribution guidelines, use the official repository:

[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend)

## License

This plugin is distributed under the GNU General Public License v3.0. See
[LICENSE](./LICENSE).
