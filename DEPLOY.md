# SeasonSplit deployment

Images are published from your forks on every push to the `seasonsplit` branch.

- `ghcr.io/gemivnet/sonarr-seasonsplit:latest`  (fork of Sonarr's v5 / .NET 10 line)
- `ghcr.io/gemivnet/rdt-client-seasonsplit:latest`  (fork of rogerfar/rdt-client)

> The `rdt-client` fork is the download client the author pairs with this, but the
> multi-season feature itself works with any download client — see
> [FORK.md](./FORK.md#companion-rdt-client-fork).

## One-time: make the packages public

Both packages are private by default. Either:

- Visit https://github.com/users/gemivnet/packages/container/sonarr-seasonsplit/settings and set visibility to Public.
- Repeat for `rdt-client-seasonsplit`.

Or pull privately by `docker login ghcr.io -u gemivnet -p <PAT-with-read:packages>` from your homelab host.

## Drop-in replacement

Swap your existing `image:` lines:

```yaml
services:
  sonarr:
    image: ghcr.io/gemivnet/sonarr-seasonsplit:latest
    container_name: sonarr
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      - /your/path/sonarr/config:/config
      - /your/path/downloads:/downloads
      - /your/path/tv:/tv
    ports:
      - 8989:8989
    restart: unless-stopped

  rdt-client:
    image: ghcr.io/gemivnet/rdt-client-seasonsplit:latest
    container_name: rdt-client
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      - /your/path/rdt-client/config:/data/db
      - /your/path/downloads:/data/downloads
    ports:
      - 6500:6500
    restart: unless-stopped
```

Your existing `/config` (Sonarr) and `/data/db` (rdt-client) volumes are
schema-compatible — no migration required.

## Verifying it works

There are no special `[SeasonSplit]` log lines — the fork only changes which
releases Sonarr accepts. To confirm the behaviour:

1. Find a release that spans multiple seasons (e.g. `Show.S01-S05.COMPLETE...`).
   On mainline Sonarr it is rejected ("multiple seasons"); on this fork it is
   eligible and can be grabbed.
2. Grab it. Sonarr sends it to the download client as a **single** download —
   it does not fan out into one download per season.
3. On completion, Sonarr imports the pack and places every season's episodes,
   because per-file import maps each file in the pack to its episode.

If a multi-season pack is still being rejected, confirm you are running the
`seasonsplit` image (the flag `SeasonSplitConfig.AllowMultiSeasonPacks` is what
allows it).

## seasonsplitarr

The standalone `seasonsplitarr` middleware that an earlier design relied on has
been retired and archived — it is no longer part of this setup. If you still have
it running, stop the container and remove its indexer entry from Prowlarr; your
real indexers and the rdt-client download client are unaffected.

## Notes

- **Linux/amd64 only** right now. ARM is not built — add it to the workflow's
  `platforms:` line (`.github/workflows/seasonsplit-image.yml`) if you need it.

## Rebuild

Push any commit to the `seasonsplit` branch on either fork and a new `:latest`
image publishes within a few minutes.
