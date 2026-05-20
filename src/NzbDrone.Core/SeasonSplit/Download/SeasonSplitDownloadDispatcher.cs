using System;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeasonSplit.Detection;

namespace NzbDrone.Core.SeasonSplit.Download
{
    public interface ISeasonSplitDownloadDispatcher
    {
        // Returns true if the release is a synthetic season-split release.
        // When true, MaybeIntercept has recorded the grab in the store and
        // rewritten the release's MagnetUrl/InfoHash so the download client
        // tracks it as a distinct torrent. Callers proceed to the normal
        // download client dispatch with the (mutated) release.
        bool MaybeIntercept(RemoteEpisode remoteEpisode);
    }

    public sealed class SeasonSplitDownloadDispatcher : ISeasonSplitDownloadDispatcher
    {
        public const string SyntheticGuidPrefix = "seasonsplit-";

        private static readonly Regex MagnetHashRegex =
            new(@"xt=urn:btih:([A-Fa-f0-9]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled);

        private readonly ISeasonPackDetector _detector;
        private readonly ISeasonSplitGrabStore _store;
        private readonly Logger _logger;

        public SeasonSplitDownloadDispatcher(
            ISeasonPackDetector detector,
            ISeasonSplitGrabStore store,
            Logger logger)
        {
            _detector = detector;
            _store = store;
            _logger = logger;
        }

        public bool MaybeIntercept(RemoteEpisode remoteEpisode)
        {
            var release = remoteEpisode?.Release;
            if (release == null || string.IsNullOrEmpty(release.Guid) ||
                !release.Guid.StartsWith(SyntheticGuidPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (release is not TorrentInfo torrent)
            {
                _logger.Warn("Season-split: guid {0} on a non-torrent release; ignoring", release.Guid);
                return false;
            }

            // Pick season from the (single-element) Seasons collection that
            // the expander populated. Fall back to re-detecting from the
            // title if anything looks off.
            var season = ResolveSeason(remoteEpisode, torrent);
            if (season <= 0)
            {
                _logger.Warn("Season-split: could not resolve season for guid {0}", release.Guid);
                return false;
            }

            var realHash = torrent.InfoHash;
            if (string.IsNullOrEmpty(realHash))
            {
                _logger.Warn("Season-split: guid {0} has no infohash; cannot intercept", release.Guid);
                return false;
            }

            var syntheticHash = _detector.SyntheticInfohash(realHash, season);
            var sourceMagnet = torrent.MagnetUrl ?? string.Empty;

            _store.Put(new SeasonSplitGrab
            {
                SyntheticGuid = release.Guid,
                SyntheticInfoHash = syntheticHash,
                RealInfoHash = realHash,
                Season = season,
                SourceMagnet = sourceMagnet,
            });

            // Rewrite the magnet + infohash so the download client sees a
            // distinct torrent per season. The synthetic magnet carries two
            // extra `x.` parameters that the rdt-client fork picks up:
            //   x.realmagnet=<urlencoded original magnet> — the magnet to
            //     send to Real-Debrid (vanilla magnet parsers will ignore
            //     the unknown `x.` param).
            //   x.includeseasons=<n>                    — season number; rdt
            //     turns this into an IncludeRegex so only that season's
            //     files materialise.
            torrent.InfoHash = syntheticHash;
            if (!string.IsNullOrEmpty(sourceMagnet))
            {
                var synthMagnet = MagnetHashRegex.Replace(sourceMagnet, $"xt=urn:btih:{syntheticHash}", 1);
                var encodedReal = Uri.EscapeDataString(sourceMagnet);
                torrent.MagnetUrl = $"{synthMagnet}&x.realmagnet={encodedReal}&x.includeseasons={season}";
            }

            _logger.Info("[SeasonSplit] Intercepted grab: guid={0} title='{1}' real-infohash={2} synth-infohash={3} season=S{4:D2} indexer={5}",
                release.Guid, release.Title, realHash, syntheticHash, season, release.Indexer);

            var siblings = _store.SiblingsOf(realHash);
            if (siblings.Count > 1)
            {
                _logger.Info("[SeasonSplit] {0} now has {1} sibling season grabs in store: {2}",
                    realHash, siblings.Count, string.Join(", ", siblings.Select(s => $"S{s.Season:D2}")));
            }

            return true;
        }

        private int ResolveSeason(RemoteEpisode remoteEpisode, TorrentInfo torrent)
        {
            if (remoteEpisode?.Episodes != null)
            {
                foreach (var ep in remoteEpisode.Episodes)
                {
                    if (ep.SeasonNumber > 0)
                    {
                        return ep.SeasonNumber;
                    }
                }
            }

            // Last resort: title still encodes S{NN} via the synthetic rewrite.
            var m = Regex.Match(torrent.Title ?? string.Empty, @"\bS(\d{1,2})\b", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s) && s > 0)
            {
                return s;
            }

            return 0;
        }
    }
}
