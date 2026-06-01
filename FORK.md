# Sonarr — seasonsplit fork

> This is a **fork of [Sonarr](https://github.com/Sonarr/Sonarr)** with one added
> capability: it lets Sonarr **accept and import a multi-season torrent pack as a
> single download**. Everything else is mainline Sonarr; for normal usage see the
> upstream [README](./README.md) and [wiki](https://wiki.servarr.com/sonarr).

## Why this fork exists

Mainline Sonarr [will not support multi-season packs natively](https://github.com/Sonarr/Sonarr/issues/1007):
it rejects any release that spans more than one season, at both the grab decision
and the import stage. So a release like `Show.S01-S05.COMPLETE.1080p` can never be
grabbed and imported as-is.

This fork flips that single decision. Multi-season packs are allowed through, the
whole pack is grabbed as **one** download, and Sonarr's normal per-file import maps
each file in the pack onto the right episode across all of its seasons.

> **History.** An earlier version of this fork was much larger: it generated
> synthetic per-season releases, rewrote qBittorrent magnets, shipped extra form
> params to a companion `rdt-client` fork, kept a JSON grab store, and ran an
> auto-blocklist subsystem — alongside a separate middleware service,
> `seasonsplitarr`. All of that has been removed in favour of the minimal
> "accept the pack, import per file" approach documented here, and
> `seasonsplitarr` is archived. If you are reading older docs or commit
> messages that mention any of those pieces, they no longer exist.

## What the fork changes

The entire delta is one config toggle plus three one-line guards that read it:

| File | Change |
|---|---|
| `src/NzbDrone.Core/SeasonSplit/SeasonSplitConfig.cs` | **New file.** A single static flag: `SeasonSplitConfig.AllowMultiSeasonPacks => true`. |
| `src/NzbDrone.Core/DecisionEngine/Specifications/MultiSeasonSpecification.cs` | Skip the multi-season rejection at grab time when the flag is on. |
| `src/NzbDrone.Core/Parser/ParsingService.cs` | When the flag is on, map a multi-season release to every episode across all its parsed seasons, so one download covers the whole pack and Sonarr won't separately re-grab the other seasons. |
| `src/NzbDrone.Core/MediaFiles/DownloadedEpisodesImportService.cs` | Skip the multi-season rejection at import time when the flag is on; let per-file import place each file. |

There are **no** new services, no DI/composition edits, no download-client or
qBittorrent hooks, and no background watchers. The flag is a static default so the
three core edits stay one-liners — promote it to an `IConfigService` setting + UI
later if you want it toggleable.

## How it works, end to end

1. A search returns a multi-season pack (`ParsedEpisodeInfo.IsMultiSeason`).
2. `MultiSeasonSpecification` no longer vetoes it, so it can win the decision and
   be grabbed — as a single, ordinary download through your existing download
   client.
3. `ParsingService.GetEpisodes` maps that one release to every episode in all of
   its seasons, so the grab satisfies the whole pack at once (no per-season
   re-grab).
4. When the download completes, `DownloadedEpisodesImportService` accepts the
   multi-season download and Sonarr's normal per-file import routes each file to
   its episode.

No special log lines are emitted — the behaviour is just the absence of the
mainline "multi-season rejected" messages, plus a normal multi-episode import.

## Keeping up to date

This fork tracks upstream Sonarr's `main` branch (the v5 / .NET 10 line — the tree
targets `net10.0`, ships `Sonarr.Api.V5`, and builds with the .NET 10 SDK).

```bash
git remote add upstream https://github.com/Sonarr/Sonarr.git   # one time
git fetch upstream
git rebase upstream/main seasonsplit
# Conflicts, if any, are confined to the four files above. SeasonSplitConfig.cs
# is a new file and never conflicts.
```

## Docker images

Pushed automatically on every commit to the `seasonsplit` branch by
[`.github/workflows/seasonsplit-image.yml`](.github/workflows/seasonsplit-image.yml):

- `ghcr.io/gemivnet/sonarr-seasonsplit:latest`

See [DEPLOY.md](./DEPLOY.md) for a drop-in `docker compose` example.

## Companion rdt-client fork

The author runs this next to **[gemivnet/rdt-client](https://github.com/gemivnet/rdt-client)**
(branch `seasonsplit`) as the download client, but the season-split behaviour above
does **not** depend on it — it works with any download client that hands Sonarr the
pack's files for import. That fork is a separate, TorBox-focused fork; see its
[`SEASON_SPLIT.md`](https://github.com/gemivnet/rdt-client/blob/seasonsplit/SEASON_SPLIT.md).
