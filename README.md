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
- Broadcast frame rate and aspect ratio, enriched by a short probe of buffered
  HTSP output for interlacing, HDR, bit depth, codec profile, and bitrate. The
  probe keeps the complete track list, opens no additional tuner subscription,
  and caches results for 30 minutes with format-change detection. Probe failures
  leave playback available; the Force deinterlace setting still takes precedence.
- TVHeadend channel tags imported into Jellyfin, including tag renames/deletions.
  Channel deletion removes the cached channel and its guide immediately. Channel
  and tag changes use the existing throttled guide refresh; duplicate metadata
  and current/next-programme pointers do not trigger refreshes.
- EPG cached from HTSP event pushes on the existing metadata connection, with
  partial updates, deletions and a fresh snapshot after reconnect. Guide reads
  reuse that cache instead of requesting each channel separately. Changes are
  coalesced for 30 seconds and use Jellyfin's guide refresh task at most once per
  12 hours; an initial dump does not trigger another refresh.
- Audio and subtitle languages preserved from TVHeadend, with probe fallback for
  missing values and consistent ISO-639 codes in Jellyfin and MPEG-TS descriptors.
- Channel logos and programme artwork fetched from TVHeadend's image cache over
  the existing HTSP connection, with validated local caching and pruning. Other
  same-server image paths retain HTTP; external artwork URLs remain external.
  HTSP file access needs TVHeadend's HTSP recorder permission; accounts without
  it retain the authenticated HTTP artwork path.
- An in-plugin MPEG-TS muxer for HTSP payloads, including common video, audio,
  DVB subtitle, teletext, and private/fallback stream handling.
- Signal monitoring and recovery for HTSP streams, including lock/SNR/UNC
  tracking, damaged video withholding until a clean keyframe, and bounded
  reconnects.
- Runtime status in the plugin settings page: connection state, active tuners,
  reader counts, signal metrics, queue health, drops, reconnects, startup cache
  state, and per-stream packet/event counters.
- A Test connection button for unsaved HTSP, HTTP ticket and HTTP basic settings.
  HTTP tests check HTSP access and briefly probe an accessible channel at low
  priority; results cover connection/authentication failures without saving settings.
- Jellyfin 12 / .NET 10 packaging metadata.

## Requirements

- Jellyfin server compatible with plugin ABI `12.0.0.0`.
- TVHeadend with HTSP access enabled. HTTP access is needed for recordings,
  HTTP streaming modes, and image URLs outside TVHeadend's image cache.
- .NET 10 SDK to build from source.

## Installation

Add this URL as a plugin repository in the Jellyfin dashboard:

```text
https://raw.githubusercontent.com/thor2002ro/jellyfin-plugin-tvheadend/manifest/manifest.json
```

The `manifest` branch is generated from published GitHub releases. Only releases
with a valid `TVHeadEnd_<version>.zip` asset are listed. The automation keeps that
branch to one amended `Local: Update plugin repository manifest` commit.

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

The project targets `net10.0`. A Release build generates a timestamp version in
the system's local timezone (`yyyy.M.d.HHmm`) and produces both the plugin DLL
and an installable `TVHeadEnd_<version>.zip` archive:

```powershell
dotnet build --configuration Release
```

Pass `-p:Version=<version>` when an exact version must be reproduced.

The ZIP contains `TVHeadEnd.dll` and is the asset to use in a Jellyfin plugin
repository manifest. The standalone DLL remains available for manual installs.

GitHub Actions does not compile or package the plugin. To publish a release:

1. Build the Release ZIP locally.
2. Create or open a draft GitHub release for that version.
3. Upload `TVHeadEnd_<version>.zip` to the draft.
4. Publish the release only after the upload completes.

Publishing the release triggers the manifest workflow, which downloads and
validates the uploaded ZIP without replacing or rebuilding it. If the ZIP is
added after publication, run the manifest workflow manually.

## Upstream

This fork is based on the Jellyfin TVHeadend plugin. For upstream issues,
documentation, and contribution guidelines, use the official repository:

[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend)

## License

This plugin is distributed under the GNU General Public License v3.0. See
[LICENSE](./LICENSE).
