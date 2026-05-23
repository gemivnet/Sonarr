# SeasonSplit deployment

Images are published from your forks on every push to the `seasonsplit` branch.

- `ghcr.io/gemivnet/sonarr-seasonsplit:latest`  (forked from Sonarr v4.0.9.2513)
- `ghcr.io/gemivnet/rdt-client-seasonsplit:latest`  (forked from rogerfar/rdt-client)

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
      - TZ=America/Chicago
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
      - TZ=America/Chicago
    volumes:
      - /your/path/rdt-client/config:/data/db
      - /your/path/downloads:/data/downloads
    ports:
      - 6500:6500
    restart: unless-stopped
```

Your existing `/config` (Sonarr) and `/data/db` (rdt-client) volumes are
schema-compatible — no migration required.

## What to watch in the logs

`docker logs -f sonarr` should show:

```
[SeasonSplit] Grab store ready at /config/seasonsplit-grabs.json (0 existing grabs loaded)
[AutoBlocklist] PermanentClientErrorWatcher initialised (markers: infringing_file, unknown_resource, permission_denied, 451, 403, 404)
[AutoBlocklist] StalledDownloadWatcher initialised (threshold: 6h)
[AutoBlocklist] ImportFailureWatcher initialised (max retries: 3)
```

When a search hits a multi-season pack:

```
[SeasonSplit] Expanded pack 'Show.S01-S05.COMPLETE.1080p.WEB-DL' -> 5 synthetic releases S01-S05 (real infohash abc123..., per-season size 12345678 bytes, indexer Some-Tracker)
[SeasonSplit] Returning 47 original + 5 synthetic releases (1 packs expanded)
```

When Sonarr grabs one of the synthetic releases:

```
[SeasonSplit] Intercepted grab: guid=seasonsplit-... title='Show.S03.1080p.WEB-DL' real-infohash=abc123... synth-infohash=def456... season=S03 indexer=Some-Tracker
```

`docker logs -f rdt-client` should show:

```
[SeasonSplit] Magnet-embedded seasons=3 -> IncludeRegex='(?i)\bS03\b'
[SeasonSplit] Using real magnet for debrid (length=NNN), local synth hash from urls (length=MMM)
```

When the auto-blocklist kicks in, Sonarr logs:

```
[AutoBlocklist] Permanent client error on Some.Pack: infringing_file — marking failed
[AutoBlocklist] Download stalled for 6h on Some.Pack — marking failed
[AutoBlocklist] 3 import failures on Some.Pack — marking failed
```

## Retiring seasonsplitarr

Once you've validated the above end-to-end:

1. Stop the seasonsplitarr container.
2. In Prowlarr, remove the seasonsplitarr indexer entry (real indexers
   stay; Sonarr v4 fork talks to them via Prowlarr as before).
3. In Sonarr, the download client is still pointed at rdt-client (qBit-
   protocol), so no change there.

The `.state.json` and cache dirs under `seasonsplitarr`'s `SS_DOWNLOADS_DIR`
can be deleted — fork uses its own JSON store at `/config/seasonsplit-grabs.json`.

## Known limitations

- Linux/amd64 only right now. ARM not built (add to the workflow's
  `platforms:` line if you need it).
- `AutoBlocklistConfig` thresholds (6h stall, 3 import retries, RD error
  markers) are static defaults; no UI yet.
- Synthetic infohash trick relies on the embedded `x.realmagnet=` param
  surviving as a form value through Sonarr's qBit proxy. Vanilla
  qBittorrent will ignore the `x.` param; only the rdt-client fork reads
  it. Don't point Sonarr-fork at a non-forked qBit/rdt-client for
  season-split grabs.

## Rebuild

Push any commit to the `seasonsplit` branch on either fork and a new
`:latest` image publishes within ~3 minutes.
