using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeasonSplit.Detection;

namespace NzbDrone.Core.SeasonSplit
{
    public interface ISeasonSplitReleaseExpander
    {
        // Walks an indexer batch and appends synthetic per-season clones for
        // any release whose title encodes a multi-season pack. Originals are
        // preserved — downstream decision logic decides which wins.
        // wantedSeasons (when non-null) is the season(s) the current search asked
        // for. It's recorded for logging but no longer limits the clones: a pack
        // frequently only surfaces under one season's indexer query (an "S01-03"
        // title matches an S01 search but not S02/S03), so every season in the
        // pack is emitted and the decision engine picks the right one(s).
        // seriesTvdbId (when > 0) is stamped onto every synthetic so the decision
        // engine can map it to the searched series by TvdbId — pack release names
        // ("Show.Part 2/2.S10…DrM") often don't clean-match the series title, and
        // without this they'd be rejected as "Unknown Series".
        IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases, IReadOnlyCollection<int> wantedSeasons = null, int seriesTvdbId = 0);
    }

    public sealed class SeasonSplitReleaseExpander : ISeasonSplitReleaseExpander
    {
        private readonly ISeasonPackDetector _detector;
        private readonly Logger _logger;

        public SeasonSplitReleaseExpander(ISeasonPackDetector detector, Logger logger)
        {
            _detector = detector;
            _logger = logger;
        }

        public IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases, IReadOnlyCollection<int> wantedSeasons = null, int seriesTvdbId = 0)
        {
            if (releases == null || releases.Count == 0)
            {
                return releases;
            }

            // Dedupe candidate packs by infohash so two indexers carrying the
            // same release don't get split twice.
            var seenHashes = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var synthetics = new List<ReleaseInfo>();
            var packsDetected = 0;

            foreach (var release in releases)
            {
                if (release is not TorrentInfo torrent)
                {
                    continue;
                }

                // We need *some* way to obtain the real torrent at grab time:
                // either a magnet/infohash already in the feed (The Pirate Bay),
                // or a download URL the dispatcher can resolve to a magnet via the
                // indexer's 302 redirect (most Prowlarr-proxied indexers). Without
                // either there's nothing to split.
                var hasInfoHash = !string.IsNullOrEmpty(torrent.InfoHash);
                var hasFetchable = !string.IsNullOrEmpty(torrent.MagnetUrl) || !string.IsNullOrEmpty(torrent.DownloadUrl);
                if (!hasFetchable)
                {
                    continue;
                }

                var range = _detector.Detect(torrent.Title ?? string.Empty);
                if (range == null)
                {
                    continue;
                }

                // Dedupe by infohash when known, otherwise by guid, so the same
                // pack carried by two indexers isn't split twice.
                var dedupeKey = hasInfoHash ? torrent.InfoHash : torrent.Guid;
                if (!string.IsNullOrEmpty(dedupeKey) && !seenHashes.Add(dedupeKey))
                {
                    _logger.Debug("[SeasonSplit] Skipping duplicate pack (already seen {0}): {1}", dedupeKey, torrent.Title);
                    continue;
                }

                // The synthetic guid must be deterministic and unique per
                // (source, season). Seed it from the infohash when present so a
                // grab matches the dispatcher's bookkeeping; fall back to the
                // source guid for magnet-less (Prowlarr) releases.
                var guidSeed = hasInfoHash ? torrent.InfoHash : (torrent.Guid ?? torrent.DownloadUrl ?? string.Empty);

                packsDetected++;
                var perSeasonSize = torrent.Size > 0 ? torrent.Size / range.Count : 0;

                // Emit a synthetic for EVERY season in the pack, not just the one
                // the current search asked for. A multi-season pack often only
                // surfaces under a single season's indexer query (an "S01-03"
                // title matches an S01 search but not S02/S03), so limiting to the
                // searched season would leave the pack's other seasons impossible
                // to grab. Emitting them all lets the user pick any season from the
                // one search the pack appears in; a wrong-season clone is simply
                // rejected by the decision engine on a single-season search, and is
                // wanted in an RSS / full-series run.
                for (var season = range.Start; season <= range.End; season++)
                {
                    synthetics.Add(CreateSynthetic(torrent, range, season, perSeasonSize, seriesTvdbId, guidSeed));
                }

                var wantedNote = wantedSeasons is { Count: > 0 } ? string.Join(",", wantedSeasons) : "all";
                var realSource = hasInfoHash ? $"infohash {torrent.InfoHash}" : "download-url (magnet resolved at grab time)";
                _logger.Info("[SeasonSplit] Expanded pack '{0}' -> {1} synthetic release(s) for S{2:D2}-S{3:D2} (search wanted: {4}; real source: {5}, per-season size {6} bytes, indexer {7})", torrent.Title, range.Count, range.Start, range.End, wantedNote, realSource, perSeasonSize, torrent.Indexer);
            }

            if (synthetics.Count == 0)
            {
                _logger.Debug("[SeasonSplit] No multi-season packs in batch of {0} releases", releases.Count);
                return releases;
            }

            _logger.Info("[SeasonSplit] Returning {0} original + {1} synthetic releases ({2} packs expanded)", releases.Count, synthetics.Count, packsDetected);

            var result = new List<ReleaseInfo>(releases.Count + synthetics.Count);
            result.AddRange(releases);
            result.AddRange(synthetics);
            return result;
        }

        private TorrentInfo CreateSynthetic(TorrentInfo source, SeasonRange range, int season, long perSeasonSize, int seriesTvdbId, string guidSeed)
        {
            // Each sibling season would otherwise re-fetch the SAME indexer
            // /download link (one Prowlarr call per season → 429 rate-limits on
            // a multi-season pack). All siblings resolve to the same torrent, so
            // when a magnet is available, grab via the magnet directly and skip
            // the indexer download endpoint entirely. Falls back to the indexer
            // URL only when there's no magnet.
            var downloadUrl = !string.IsNullOrEmpty(source.MagnetUrl) ? source.MagnetUrl : source.DownloadUrl;

            return new TorrentInfo
            {
                Guid = _detector.SyntheticGuid(guidSeed, season),
                Title = _detector.SyntheticTitle(source.Title ?? string.Empty, range, season),
                Size = perSeasonSize,
                DownloadUrl = downloadUrl,
                InfoUrl = source.InfoUrl,
                CommentUrl = source.CommentUrl,
                IndexerId = source.IndexerId,
                Indexer = source.Indexer,
                IndexerPriority = source.IndexerPriority,
                DownloadProtocol = source.DownloadProtocol,

                // Prefer the searched series' TvdbId so the decision engine maps
                // by id and skips fragile title→series matching (pack names rarely
                // clean-match). Fall back to whatever the source carried.
                TvdbId = seriesTvdbId > 0 ? seriesTvdbId : source.TvdbId,
                TvRageId = source.TvRageId,
                ImdbId = source.ImdbId,
                PublishDate = source.PublishDate,
                Origin = source.Origin,
                Source = source.Source,
                Container = source.Container,
                Codec = source.Codec,
                Resolution = source.Resolution,
                Languages = source.Languages?.ToList() ?? new List<Language>(),
                IndexerFlags = source.IndexerFlags,

                // Carry the real infohash through. The download dispatcher
                // uses (InfoHash, season) to coordinate sibling grabs against
                // a single underlying download.
                InfoHash = source.InfoHash,
                MagnetUrl = source.MagnetUrl,
                Seeders = source.Seeders,
                Peers = source.Peers,
            };
        }
    }
}
