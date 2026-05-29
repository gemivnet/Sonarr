using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaFiles.TorrentInfo;
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

        private static readonly Regex MagnetHashRegex = new Regex(@"xt=urn:btih:([A-Fa-f0-9]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled);
        private static readonly Regex IncludeRegexParam = new Regex(@"[&?]x\.includeregex=([^&]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ISeasonPackDetector _detector;
        private readonly ISeasonSplitGrabStore _store;
        private readonly IHttpClient _httpClient;
        private readonly ITorrentFileInfoReader _torrentFileInfoReader;
        private readonly Logger _logger;

        public SeasonSplitDownloadDispatcher(ISeasonPackDetector detector,
                                             ISeasonSplitGrabStore store,
                                             IHttpClient httpClient,
                                             ITorrentFileInfoReader torrentFileInfoReader,
                                             Logger logger)
        {
            _detector = detector;
            _store = store;
            _httpClient = httpClient;
            _torrentFileInfoReader = torrentFileInfoReader;
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

            // The synthetic infohash is encoded in the guid ("seasonsplit-<hash>")
            // by the expander — it's the single source of truth, so reading it
            // back here keeps the qBit identity consistent regardless of whether
            // the expander seeded from a real infohash (magnet feeds) or the
            // source guid (magnet-less Prowlarr releases).
            var syntheticHash = release.Guid.Substring(SyntheticGuidPrefix.Length);
            if (string.IsNullOrEmpty(syntheticHash))
            {
                _logger.Warn("Season-split: guid {0} has no synthetic hash; cannot intercept", release.Guid);
                return false;
            }

            // The real magnet is what we ship to the debrid provider. Magnet
            // feeds (The Pirate Bay) carry it directly; Prowlarr-proxied releases
            // only give a /download URL that 302-redirects to the magnet — resolve
            // that now (the user is grabbing exactly one release, so this is a
            // single fetch, not the per-season fan-out we deliberately avoid).
            var realMagnet = !string.IsNullOrEmpty(torrent.MagnetUrl)
                ? torrent.MagnetUrl
                : ResolveRealMagnet(torrent, release);

            if (string.IsNullOrEmpty(realMagnet))
            {
                _logger.Warn("[SeasonSplit] Could not obtain a magnet for guid {0} (indexer {1}); leaving release un-split", release.Guid, release.Indexer);
                return false;
            }

            // Add Magnet grabs carry an explicit include filter (it can mix whole
            // seasons and individual episodes) on the magnet as x.includeregex.
            // Pull it off so the download client ships it verbatim, then strip it
            // from the real magnet we hand to the provider.
            var includeRegex = ExtractIncludeRegex(realMagnet);
            if (includeRegex != null)
            {
                realMagnet = StripIncludeRegex(realMagnet);
            }

            var realHash = ExtractBtih(realMagnet) ?? torrent.InfoHash ?? string.Empty;

            // All distinct seasons this grab covers. A normal per-season grab has
            // one; a consolidated Add Magnet grab spans several, so the include
            // regex becomes the union (one torrent, all wanted seasons).
            var seasons = ResolveSeasons(remoteEpisode, season);

            _store.Put(new SeasonSplitGrab
            {
                SyntheticGuid = release.Guid,
                SyntheticInfoHash = syntheticHash,
                RealInfoHash = realHash,
                Season = season,
                Seasons = seasons,
                IncludeRegex = includeRegex,
                SourceMagnet = realMagnet,
            });

            // Rewrite the magnet + infohash so the download client sees a
            // distinct torrent per season. The synthetic magnet carries two
            // extra `x.` parameters that the rdt-client fork picks up:
            //   x.realmagnet=<urlencoded original magnet> — the magnet to
            //     send to the debrid provider (vanilla magnet parsers will
            //     ignore the unknown `x.` param).
            //   x.includeseasons=<n>                    — season number; rdt
            //     turns this into an IncludeRegex so only that season's
            //     files materialise.
            var synthMagnet = MagnetHashRegex.Replace(realMagnet, $"xt=urn:btih:{syntheticHash}", 1);
            if (!synthMagnet.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                synthMagnet = $"magnet:?xt=urn:btih:{syntheticHash}";
            }

            var encodedReal = Uri.EscapeDataString(realMagnet);
            var finalMagnet = $"{synthMagnet}&x.realmagnet={encodedReal}&x.includeseasons={string.Join(",", seasons)}";

            torrent.InfoHash = syntheticHash;
            torrent.MagnetUrl = finalMagnet;

            // Force the magnet path: a magnet-less release still has its original
            // /download URL here, which would otherwise pull the whole pack.
            torrent.DownloadUrl = finalMagnet;

            _logger.Info("[SeasonSplit] Intercepted grab: guid={0} title='{1}' real-infohash={2} synth-infohash={3} season=S{4:D2} indexer={5}", release.Guid, release.Title, realHash, syntheticHash, season, release.Indexer);

            var siblings = _store.SiblingsOf(realHash);
            if (siblings.Count > 1)
            {
                _logger.Info("[SeasonSplit] {0} now has {1} sibling season grabs in store: {2}", realHash, siblings.Count, string.Join(", ", siblings.Select(s => $"S{s.Season:D2}")));
            }

            return true;
        }

        // Resolve a magnet-less (Prowlarr-proxied) release to its real magnet by
        // fetching the /download URL without following redirects and reading the
        // magnet out of the Location header — the same trick TorrentClientBase
        // uses. Falls back to downloading the .torrent and synthesising a magnet
        // from its infohash when the indexer serves a file instead of redirecting.
        private string ResolveRealMagnet(TorrentInfo torrent, ReleaseInfo release)
        {
            var url = torrent.DownloadUrl;
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            try
            {
                var request = new HttpRequest(url)
                {
                    AllowAutoRedirect = false,
                };
                request.Headers.Accept = "application/x-bittorrent";

                if (release.IndexerId > 0)
                {
                    request.RateLimitKey = release.IndexerId.ToString();
                }

                var response = _httpClient.Get(request);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
                {
                    var location = response.Headers.GetSingleValue("Location");
                    if (!string.IsNullOrEmpty(location) && location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("[SeasonSplit] Resolved magnet for '{0}' via redirect", release.Title);
                        return location;
                    }

                    _logger.Warn("[SeasonSplit] /download for '{0}' redirected to a non-magnet location; cannot split", release.Title);
                    return null;
                }

                // Indexer served the .torrent directly — derive the infohash and
                // build a magnet (the debrid provider resolves by infohash).
                var data = response.ResponseData;
                if (data is { Length: > 0 })
                {
                    var hash = _torrentFileInfoReader.GetHashFromTorrentFile(data);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        var dn = Uri.EscapeDataString(release.Title ?? string.Empty);
                        return $"magnet:?xt=urn:btih:{hash}&dn={dn}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[SeasonSplit] Failed to resolve magnet from download URL for '{0}'", release.Title);
            }

            return null;
        }

        private static string ExtractIncludeRegex(string magnet)
        {
            var m = IncludeRegexParam.Match(magnet ?? string.Empty);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }

        private static string StripIncludeRegex(string magnet)
        {
            return IncludeRegexParam.Replace(magnet ?? string.Empty, string.Empty);
        }

        private static string ExtractBtih(string magnet)
        {
            if (string.IsNullOrEmpty(magnet))
            {
                return null;
            }

            var m = MagnetHashRegex.Match(magnet);
            return m.Success ? m.Groups[1].Value : null;
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

        // Every distinct season the grab's episodes belong to (ascending). One
        // entry for a normal per-season grab; the full set for a consolidated
        // Add Magnet grab. Falls back to [primarySeason].
        private static IReadOnlyList<int> ResolveSeasons(RemoteEpisode remoteEpisode, int primarySeason)
        {
            var seasons = remoteEpisode?.Episodes?
                .Select(e => e.SeasonNumber)
                .Where(s => s > 0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (seasons != null && seasons.Count > 0)
            {
                return seasons;
            }

            return primarySeason > 0 ? new[] { primarySeason } : Array.Empty<int>();
        }
    }
}
