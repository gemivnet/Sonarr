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
        // wantedSeasons (when non-null) limits the clones to those season
        // numbers — the current search only needs those, and emitting the whole
        // pack's worth of seasons on every per-season search needlessly grows
        // the decision batch.
        IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases, IReadOnlyCollection<int> wantedSeasons = null);
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

        public IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases, IReadOnlyCollection<int> wantedSeasons = null)
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
                if (release is not TorrentInfo torrent || string.IsNullOrEmpty(torrent.InfoHash))
                {
                    continue;
                }

                var range = _detector.Detect(torrent.Title ?? string.Empty);
                if (range == null)
                {
                    continue;
                }

                if (!seenHashes.Add(torrent.InfoHash))
                {
                    _logger.Debug("[SeasonSplit] Skipping duplicate pack (already seen infohash {0}): {1}", torrent.InfoHash, torrent.Title);
                    continue;
                }

                packsDetected++;
                var perSeasonSize = torrent.Size > 0 ? torrent.Size / range.Count : 0;
                var emitted = 0;

                for (var season = range.Start; season <= range.End; season++)
                {
                    // Only emit the season(s) the current search actually wants —
                    // a per-season search has no use for the pack's other seasons,
                    // and emitting them all just bloats the decision batch.
                    if (wantedSeasons != null && !wantedSeasons.Contains(season))
                    {
                        continue;
                    }

                    synthetics.Add(CreateSynthetic(torrent, range, season, perSeasonSize));
                    emitted++;
                }

                if (emitted == 0)
                {
                    packsDetected--;
                    continue;
                }

                _logger.Info("[SeasonSplit] Expanded pack '{0}' -> {1} synthetic release(s) within S{2:D2}-S{3:D2} (real infohash {4}, per-season size {5} bytes, indexer {6})", torrent.Title, emitted, range.Start, range.End, torrent.InfoHash, perSeasonSize, torrent.Indexer);
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

        private TorrentInfo CreateSynthetic(TorrentInfo source, SeasonRange range, int season, long perSeasonSize)
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
                Guid = _detector.SyntheticGuid(source.InfoHash, season),
                Title = _detector.SyntheticTitle(source.Title ?? string.Empty, range, season),
                Size = perSeasonSize,
                DownloadUrl = downloadUrl,
                InfoUrl = source.InfoUrl,
                CommentUrl = source.CommentUrl,
                IndexerId = source.IndexerId,
                Indexer = source.Indexer,
                IndexerPriority = source.IndexerPriority,
                DownloadProtocol = source.DownloadProtocol,
                TvdbId = source.TvdbId,
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
