using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeasonSplit.Detection;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.SeasonSplit.Preview
{
    public sealed class MagnetSeasonPreview
    {
        public int Season { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        public string Title { get; set; }
        public DownloadDecision Decision { get; set; }
    }

    public interface IMagnetPreviewService
    {
        List<MagnetSeasonPreview> Preview(string magnetUrl, int tvdbId);
    }

    // Phase 1b: turn a pasted magnet into the per-season preview rows the UI shows.
    // Probes RD for the real file list, groups files by season for real sizes +
    // file-parsed quality, then runs each per-season synthetic release through the
    // SAME decision engine interactive search uses — so "already have / not an
    // upgrade / quality or size rejected" come out as the exact same rejections,
    // and Approved drives the default checkbox state.
    public sealed class MagnetPreviewService : IMagnetPreviewService
    {
        private static readonly Regex SeasonFromName = new Regex(@"(?i)\bS(\d{1,2})E\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex SeasonFromFolder = new Regex(@"(?i)(?:^|[/\\])Season[\s._-]*(\d{1,2})(?:[/\\]|$)", RegexOptions.Compiled);
        private static readonly Regex QualityToken = new Regex(@"(?i)\b(2160p|1080p|720p|576p|480p|360p|bluray|web[._ -]?dl|webrip|hdtv|dvdrip|bdrip|brrip|x264|x265|h[._ ]?264|h[._ ]?265|hevc|xvid)\b", RegexOptions.Compiled);
        private static readonly string[] VideoExts = { ".mkv", ".mp4", ".avi", ".ts", ".m4v", ".wmv", ".mpg", ".mpeg", ".m2ts" };

        private readonly IMagnetProbeService _probeService;
        private readonly ISeriesService _seriesService;
        private readonly ISeasonPackDetector _detector;
        private readonly IMakeDownloadDecision _decisionMaker;
        private readonly Logger _logger;

        public MagnetPreviewService(IMagnetProbeService probeService,
                                    ISeriesService seriesService,
                                    ISeasonPackDetector detector,
                                    IMakeDownloadDecision decisionMaker,
                                    Logger logger)
        {
            _probeService = probeService;
            _seriesService = seriesService;
            _detector = detector;
            _decisionMaker = decisionMaker;
            _logger = logger;
        }

        public List<MagnetSeasonPreview> Preview(string magnetUrl, int tvdbId)
        {
            var series = _seriesService.FindByTvdbId(tvdbId)
                ?? throw new InvalidOperationException($"No series in the library with TVDB id {tvdbId}; add and monitor it first");

            var probe = _probeService.Probe(magnetUrl);

            if (probe.Files == null || probe.Files.Count == 0)
            {
                _logger.Warn("[SeasonSplit] Magnet preview: probe returned no files (timedOut={0})", probe.TimedOut);
                return new List<MagnetSeasonPreview>();
            }

            var bySeason = new Dictionary<int, List<MagnetProbeFile>>();

            foreach (var file in probe.Files)
            {
                var season = SeasonOf(file.Path);

                if (season == null)
                {
                    continue;
                }

                if (!bySeason.TryGetValue(season.Value, out var list))
                {
                    list = new List<MagnetProbeFile>();
                    bySeason[season.Value] = list;
                }

                list.Add(file);
            }

            if (bySeason.Count == 0)
            {
                _logger.Warn("[SeasonSplit] Magnet preview: could not parse any season from {0} files", probe.Files.Count);
                return new List<MagnetSeasonPreview>();
            }

            var synthetics = new List<ReleaseInfo>();
            var meta = new Dictionary<string, MagnetSeasonPreview>(StringComparer.Ordinal);

            foreach (var entry in bySeason.OrderBy(kv => kv.Key))
            {
                var season = entry.Key;
                var files = entry.Value;
                var size = files.Sum(f => f.Size);
                var title = BuildTitle(series.Title, season, files);
                var guid = _detector.SyntheticGuid(probe.Hash, season);

                synthetics.Add(new TorrentInfo
                {
                    Guid = guid,
                    Title = title,
                    Size = size,
                    MagnetUrl = magnetUrl,
                    DownloadUrl = magnetUrl,
                    InfoHash = _detector.SyntheticInfohash(probe.Hash, season),
                    TvdbId = tvdbId,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    PublishDate = DateTime.UtcNow,
                    Indexer = "Add Magnet",
                });

                meta[guid] = new MagnetSeasonPreview { Season = season, Size = size, FileCount = files.Count, Title = title };
            }

            var decisions = _decisionMaker.GetRssDecision(synthetics, false);

            var result = new List<MagnetSeasonPreview>();

            foreach (var decision in decisions)
            {
                var guid = decision.RemoteEpisode?.Release?.Guid;

                if (guid != null && meta.TryGetValue(guid, out var row))
                {
                    row.Decision = decision;
                    result.Add(row);
                }
            }

            return result.OrderBy(r => r.Season).ToList();
        }

        // A parseable season-pack title that carries the REAL quality read off the
        // files — the pack name's advertised quality is frequently wrong (Bob Ross
        // pack says 720p, files are 480p).
        private static string BuildTitle(string seriesTitle, int season, List<MagnetProbeFile> files)
        {
            var rep = files.Where(f => VideoExts.Contains((Path.GetExtension(f.Path ?? string.Empty) ?? string.Empty).ToLowerInvariant()))
                           .OrderByDescending(f => f.Size)
                           .FirstOrDefault() ?? files[0];

            var fileName = Path.GetFileNameWithoutExtension(rep.Path ?? string.Empty) ?? string.Empty;
            var qm = QualityToken.Match(fileName);
            var qualitySuffix = qm.Success ? fileName.Substring(qm.Index) : string.Empty;

            var title = $"{seriesTitle} S{season:D2} {qualitySuffix}".Trim();

            return Regex.Replace(title, @"\s{2,}", " ");
        }

        private static int? SeasonOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var m = SeasonFromName.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s))
            {
                return s;
            }

            m = SeasonFromFolder.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out s))
            {
                return s;
            }

            return null;
        }
    }
}
