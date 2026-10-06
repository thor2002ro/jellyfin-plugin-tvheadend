# Jellyfin TVHeadend Plugin Fork

This repository is a fork of the official
[jellyfin/jellyfin-plugin-tvheadend](https://github.com/jellyfin/jellyfin-plugin-tvheadend)
plugin. It keeps the original TVHeadend plugin identity and adds local work aimed
at newer Jellyfin builds, more reliable HTSP playback, and better diagnostics.

Do not install this side-by-side with the upstream plugin. It is the same
Jellyfin plugin, with fork-specific changes.

## What It Includes

- Optional native Jellyfin tuners for multiple TVHeadend servers. Select
  **Recording backend > Jellyfin DVR**, add each server using the connection fields
  and **Add / update server**, then **Save** and restart Jellyfin. A native tuner and
  guide provider are registered automatically for each server; no manual guide mapping is needed.
  Server edits and mode changes require restart. Native mode uses Jellyfin's
  recording scheduler and recording directory; integrated mode remains the default
  and uses TVHeadend DVR. Existing schedules and recordings are not migrated.
  Native HTTP streams use TVHeadend ticket URLs so both playback and Jellyfin's
  TS recorder authenticate correctly, including when HTTP Basic is selected.
  HTSP keeps shared streams, track languages, recovery and programme thumbnails.
  Each server has scoped channel/programme IDs and connections; native channel
  enumeration is cached for five minutes and refreshed by a full guide refresh.
  The tuner card uses a clean endpoint URL; its stable server ID is stored in
  Jellyfin's device field. Switching to TVHeadend DVR removes generated native
  dashboard entries and retains the saved native servers.
  Stock Jellyfin-web shows each guide's server name on its secondary line.
  Its hard-coded provider-type heading remains "Unknown" for custom providers;
  no custom frontend is needed for server labels, tuning, EPG or recording.

- Jellyfin Live TV backed by TVHeadend channels, EPG data, timers, series
  timers, and recordings.
- TVHeadend recording management from Jellyfin, including DVR profiles,
  priorities, pre/post padding, and an optional synthetic "TVHeadend Recordings"
  channel.
  DVR pushes invalidate that channel's cached listing on its next request,
  including external additions, completions and deletions. Duplicate pushes
  do not invalidate; reconnects discard stale entries. The five-minute fallback
  remains in place without additional polling.
  Each cached recording also carries a UTC change timestamp for Jellyfin's
  existing modification-date mapping. Real DVR changes advance it; duplicate
  pushes do not. Reconnect snapshots establish fresh cache-local timestamps.
- Recordings reuse broadcaster artwork or existing generated programme thumbnails.
  Artwork resolved while the EPG is available is cached by server/account and
  recording ID, so it remains available after EPG expiry. Generated fallback
  follows the existing programme artwork checkbox and opens no tuner or encoder.
  Associations and copied thumbnails use the existing 90-day cache retention;
  slow artwork enrichment is capped at ten seconds and does not hide recordings.
- Streaming through HTSP, HTTP ticket URLs, or HTTP basic authentication.
- HTSP direct streaming with shared upstream subscriptions, independent buffered
  readers, clean-keyframe startup, optional initial tune buffering, and a silent
  stream watchdog.
  Stream-only connections skip unused disk/time queries. Warm subscriptions
  drop to low priority after the last viewer leaves and regain viewer priority
  on reuse, allowing channel switches to reclaim a contended tuner.
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
- Optional programme thumbnails from HTSP channels. Enable
  **Generate missing programme artwork** in plugin settings
  and save. Disabled by default; checks every 30 seconds, uses Jellyfin's encoder
  on existing buffered video and keeps broadcaster or existing artwork preferred.
  Generated pictures refresh after five minutes and include a cached local
  channel logo when available. Logo failures keep the plain frame usable.
  Captures run one at a time; watched channels use existing buffers. The optional
  **Also capture artwork from unwatched channels** checkbox permits at most one
  brief low-priority tune per minute, rotating through TV channels while no HTSP
  playback readers are active. Both checkboxes default off. Capture subscriptions
  use weight 1, create no playback readers, and close immediately after sampling;
  viewers use weight 100. Generated images use the existing 90-day image cache.
  Background capture passes group channels by learned HTSP mux identity, starting
  with the last captured mux when known. Each candidate is visited once per pass
  so other muxes and unknown channels still get a turn. Hints are scoped to the
  server/account and pruned for removed channels; TVHeadend retains physical tuner
  selection and mux sharing. This adds no playback connection pool or tuning.
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
