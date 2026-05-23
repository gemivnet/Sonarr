# Sonarr — seasonsplit fork

> This is a **fork of [Sonarr](https://github.com/Sonarr/Sonarr)** with two added
> capabilities. Everything below describes only what this fork adds on top of
> mainline Sonarr; for normal Sonarr usage see the upstream
> [README](./README.md) and [wiki](https://wiki.servarr.com/sonarr).

Companion fork: **[gemivnet/rdt-client](https://github.com/gemivnet/rdt-client)**
(branch `seasonsplit`) — the season-split download feature requires it. The two
are designed to run together.

## Why this fork exists

Mainline Sonarr [will not support multi-season torrent packs natively](https://github.com/Sonarr/Sonarr/issues/1007),
and its only response to a permanently-failed grab is a queue warning — it never
auto-blocklists and re-searches. I previously solved both with a separate
middleware service (`seasonsplitarr`) that impersonated a Torznab indexer and a
qBittorrent client. That worked but was fragile: it duplicated Sonarr's queue
model, needed synthetic-infohash and magnet-rewriting hacks, and broke whenever
Sonarr changed its qBit/Torznab handling.

This fork moves both features **inside** Sonarr, where they are first-class and
far simpler, and lets `seasonsplitarr` be retired.

### Feature 1 — Native season-pack split

A search result like `Show.S01-S05.COMPLETE.1080p` is decomposed into N
per-season releases that flow through Sonarr's normal decision/grab pipeline.
Grabbing one season tells the download client to fetch only that season's files
from the underlying pack (one Real-Debrid download shared by all siblings).

### Feature 2 — Auto-retry / blocklist

Three triggers automatically blocklist a release and re-search:
- **Permanent download-client errors** (Real-Debrid `infringing_file`, 451/403/404, etc.)
- **Stalled downloads** (no progress for a configurable number of hours)
- **Repeated import failures** (same download fails import N times)

## How it stays maintainable against upstream

All new logic lives in two dedicated namespaces so rebasing on upstream is cheap:

- `src/NzbDrone.Core/SeasonSplit/` — pack detection, synthetic release
  generation, per-season grab metadata + JSON-backed store.
- `src/NzbDrone.Core/AutoBlocklist/` — the three failure watchers.

Mainline Sonarr files are touched in only a handful of places, each a **single
hook call**, never logic:

| File | Change |
|---|---|
| `IndexerSearch/ReleaseSearchService.cs` | inject `ISeasonSplitReleaseExpander`, wrap the search-result list in `Expand(...)` |
| `Download/DownloadService.cs` | inject + call `ISeasonSplitDownloadDispatcher.MaybeIntercept(...)` |
| `Download/Clients/QBittorrent/QBittorrent.cs` | at magnet-add time, rewrite the synthetic grab's infohash + display name and ship `realMagnet`/`includeRegex` as form params |
| `Download/Clients/QBittorrent/QBittorrentProxyV1/V2.cs` + `QBittorrentProxySelector.cs` | add `AddTorrentFromUrlWithExtras(...)` (extra qBit form params; V1 ignores them) |

New services are auto-registered by Sonarr's DryIoc container (no composition
edits). New config defaults live in `AutoBlocklist/AutoBlocklistConfig.cs` as
static values (stall threshold, import-retry count, permanent-error markers) —
promote to `IConfigService` + UI when desired.

### Keeping up to date

```bash
git remote add upstream https://github.com/Sonarr/Sonarr.git   # one time
git fetch upstream --tags
git rebase <new-upstream-tag> seasonsplit
# Conflicts, if any, will be in the ~5 mainline hook files above — the
# SeasonSplit/ and AutoBlocklist/ folders are new and never conflict.
```

This fork currently branches from upstream tag **`v4.0.9.2513`**.

## End-to-end flow (with logging)

Every meaningful step logs at Info with a `[SeasonSplit]` / `[AutoBlocklist]`
prefix, so `docker logs sonarr` shows the whole path without debug mode.

1. **Search** — `SeasonSplitReleaseExpander` detects packs and emits per-season
   synthetic `TorrentInfo` clones with deterministic `seasonsplit-<sha20>` GUIDs,
   size divided by season count.
   `[SeasonSplit] Expanded pack '...' -> N synthetic releases ...`
2. **Grab dispatch** — `SeasonSplitDownloadDispatcher` records the
   `(syntheticHash, realHash, season)` triple in the grab store.
   `[SeasonSplit] Intercepted grab: guid=... season=Sxx ...`
3. **qBit add** — `QBittorrent.AddFromMagnetLink` rewrites the magnet's
   `xt=urn:btih:` to the synthetic hash and `dn=` to the per-season title (so the
   queue resolves the right episodes), and ships the real magnet + a
   `(?i)\bSxx\b` include-regex as qBit form params.
   `[SeasonSplit] qBit add: guid=... synth-title='...' (real magnet shipped as form param)`
4. **rdt-client** (the companion fork) reads those params, fetches the real pack
   from Real-Debrid once, and materialises only the matching season's files.
   `[SeasonSplit] TorrentsAdd received: ... includeRegex='(?i)\bS19\b'`
5. **Auto-blocklist** fires independently on the three triggers, e.g.
   `[AutoBlocklist] Permanent client error on ...: infringing_file — marking failed`

## Docker images

Pushed automatically on every commit to the `seasonsplit` branch by
`.github/workflows/seasonsplit-image.yml`:

- `ghcr.io/gemivnet/sonarr-seasonsplit:latest`

Pair it with `ghcr.io/gemivnet/rdt-client-seasonsplit:latest`. See that repo's
[`SEASON_SPLIT.md`](https://github.com/gemivnet/rdt-client/blob/seasonsplit/SEASON_SPLIT.md)
for the matching download-client changes.
