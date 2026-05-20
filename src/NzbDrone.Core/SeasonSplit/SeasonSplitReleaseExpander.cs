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
        IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases);
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

        public IList<ReleaseInfo> Expand(IList<ReleaseInfo> releases)
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

                for (var season = range.Start; season <= range.End; season++)
                {
                    synthetics.Add(CreateSynthetic(torrent, range, season, perSeasonSize));
                }

                _logger.Info("[SeasonSplit] Expanded pack '{0}' -> {1} synthetic releases S{2:D2}-S{3:D2} (real infohash {4}, per-season size {5} bytes, indexer {6})",
                    torrent.Title, range.Count, range.Start, range.End, torrent.InfoHash, perSeasonSize, torrent.Indexer);
            }

            if (synthetics.Count == 0)
            {
                _logger.Debug("[SeasonSplit] No multi-season packs in batch of {0} releases", releases.Count);
                return releases;
            }

            _logger.Info("[SeasonSplit] Returning {0} original + {1} synthetic releases ({2} packs expanded)",
                releases.Count, synthetics.Count, packsDetected);

            var result = new List<ReleaseInfo>(releases.Count + synthetics.Count);
            result.AddRange(releases);
            result.AddRange(synthetics);
            return result;
        }

        private TorrentInfo CreateSynthetic(TorrentInfo source, SeasonRange range, int season, long perSeasonSize)
        {
            return new TorrentInfo
            {
                Guid = _detector.SyntheticGuid(source.InfoHash, season),
                Title = _detector.SyntheticTitle(source.Title ?? string.Empty, range, season),
                Size = perSeasonSize,
                DownloadUrl = source.DownloadUrl,
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
