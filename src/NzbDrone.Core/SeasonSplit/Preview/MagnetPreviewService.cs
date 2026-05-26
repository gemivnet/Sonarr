using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
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
        public int EpisodeCount { get; set; }
        public int ExistingCount { get; set; }
        public bool Satisfied { get; set; }
        public DownloadDecision Decision { get; set; }
    }

    public sealed class MagnetGrabSkip
    {
        public int Season { get; set; }
        public string Reason { get; set; }
    }

    public sealed class MagnetGrabResult
    {
        public List<int> Grabbed { get; set; } = new List<int>();
        public List<MagnetGrabSkip> Skipped { get; set; } = new List<MagnetGrabSkip>();
    }

    public interface IMagnetPreviewService
    {
        List<MagnetSeasonPreview> Preview(string magnetUrl, int tvdbId, bool includeSatisfied = false);
        MagnetGrabResult GrabSeasons(string magnetUrl, int tvdbId, IReadOnlyCollection<int> seasons, int? downloadClientId);
    }

    // Phase 1b: turn a pasted magnet into the per-season preview rows the UI shows.
    // Probes RD for the real file list, groups files by season for real sizes +
    // file-parsed quality, then runs each per-season synthetic release through the
    // SAME decision engine interactive search uses — so "already have / not an
    // upgrade / quality or size rejected" come out as the exact same rejections,
    // and Approved drives the default checkbox state.
    public sealed class MagnetPreviewService : IMagnetPreviewService
    {
        // The indexer name stamped on every Add Magnet synthetic release. Used as
        // the "this was a manual one-off grab" marker so the auto-blocklist
        // watchers leave it alone instead of blocklisting + re-searching.
        public const string IndexerName = "Add Magnet";

        private static readonly Regex SeasonFromName = new Regex(@"(?i)\bS(\d{1,2})E\d{1,3}\b", RegexOptions.Compiled);

        // "NxNN" numbering (e.g. "07x03", "1x02") - common on non-English packs
        // (the Anthony Bourdain CZ pack uses it). The leading word boundary keeps
        // it off mid-number matches like resolutions ("1920x1080").
        private static readonly Regex SeasonFromNumberX = new Regex(@"(?i)\b(\d{1,2})x\d{2,3}\b", RegexOptions.Compiled);

        private static readonly Regex SeasonFromFolder = new Regex(@"(?i)(?:^|[/\\])Season[\s._-]*(\d{1,2})(?:[/\\]|$)", RegexOptions.Compiled);
        private static readonly Regex QualityToken = new Regex(@"(?i)\b(2160p|1080p|720p|576p|480p|360p|bluray|web[._ -]?dl|webrip|hdtv|dvdrip|bdrip|brrip|x264|x265|h[._ ]?264|h[._ ]?265|hevc|xvid)\b", RegexOptions.Compiled);
        private static readonly string[] VideoExts = { ".mkv", ".mp4", ".avi", ".ts", ".m4v", ".wmv", ".mpg", ".mpeg", ".m2ts" };

        // Size rejections are the ONE thing an override still respects — the
        // per-item max for the parsed quality (read by AcceptableSizeSpecification
        // from the profile even when the quality is disallowed). Everything else
        // (quality not wanted, not an upgrade, already have) is overridable.
        private static readonly HashSet<DownloadRejectionReason> SizeReasons = new HashSet<DownloadRejectionReason>
        {
            DownloadRejectionReason.BelowMinimumSize,
            DownloadRejectionReason.AboveMaximumSize,
            DownloadRejectionReason.MaximumSizeExceeded,
        };

        // "We already have this (or are already grabbing it) at >= the quality this
        // release offers." Any Disk/Queue/History rejection means the existing or
        // in-flight copy wins, so this release wouldn't improve the library - hide
        // it by default (even if it's ALSO rejected for e.g. quality-not-wanted).
        private static bool IsAlreadyHaveReason(DownloadRejectionReason reason)
        {
            var name = reason.ToString();

            return name.StartsWith("Disk", StringComparison.Ordinal) ||
                   name.StartsWith("Queue", StringComparison.Ordinal) ||
                   name.StartsWith("History", StringComparison.Ordinal) ||
                   reason == DownloadRejectionReason.AlreadyImportedSameHash ||
                   reason == DownloadRejectionReason.AlreadyImportedSameName;
        }

        private readonly IMagnetProbeService _probeService;
        private readonly ISeriesService _seriesService;
        private readonly ISeasonPackDetector _detector;
        private readonly IMakeDownloadDecision _decisionMaker;
        private readonly IDownloadService _downloadService;
        private readonly Logger _logger;

        public MagnetPreviewService(IMagnetProbeService probeService,
                                    ISeriesService seriesService,
                                    ISeasonPackDetector detector,
                                    IMakeDownloadDecision decisionMaker,
                                    IDownloadService downloadService,
                                    Logger logger)
        {
            _probeService = probeService;
            _seriesService = seriesService;
            _detector = detector;
            _decisionMaker = decisionMaker;
            _downloadService = downloadService;
            _logger = logger;
        }

        // Grab the user-selected seasons. The user has explicitly ticked these, so
        // we override the decision engine's "soft" rejections (quality not in
        // profile, not an upgrade, already have) - BUT still refuse anything that
        // breaks the size limit for its quality, because that's a hard ceiling the
        // user asked us to keep.
        public MagnetGrabResult GrabSeasons(string magnetUrl, int tvdbId, IReadOnlyCollection<int> seasons, int? downloadClientId)
        {
            var result = new MagnetGrabResult();
            var wanted = seasons == null ? new HashSet<int>() : new HashSet<int>(seasons);

            if (wanted.Count == 0)
            {
                return result;
            }

            // includeSatisfied: true so a season the user explicitly selected can
            // still be resolved/grabbed even if the default preview would hide it.
            foreach (var preview in Preview(magnetUrl, tvdbId, includeSatisfied: true))
            {
                if (!wanted.Contains(preview.Season))
                {
                    continue;
                }

                var decision = preview.Decision;

                if (decision?.RemoteEpisode == null)
                {
                    result.Skipped.Add(new MagnetGrabSkip { Season = preview.Season, Reason = "Could not resolve the release for this season" });
                    continue;
                }

                var sizeRejection = decision.Rejections?.FirstOrDefault(r => SizeReasons.Contains(r.Reason));

                if (sizeRejection != null)
                {
                    _logger.Info("[SeasonSplit] Add Magnet: refusing S{0:D2} on size limit: {1}", preview.Season, sizeRejection.Message);
                    result.Skipped.Add(new MagnetGrabSkip { Season = preview.Season, Reason = sizeRejection.Message });
                    continue;
                }

                _logger.Info("[SeasonSplit] Add Magnet: grabbing S{0:D2} ({1}){2}", preview.Season, preview.Title, decision.Approved ? "" : " [override]");
                _downloadService.DownloadReport(decision.RemoteEpisode, downloadClientId).GetAwaiter().GetResult();
                result.Grabbed.Add(preview.Season);
            }

            return result;
        }

        public List<MagnetSeasonPreview> Preview(string magnetUrl, int tvdbId, bool includeSatisfied = false)
        {
            var series = _seriesService.FindByTvdbId(tvdbId)
                ?? throw new InvalidOperationException($"No series in the library with TVDB id {tvdbId}; add and monitor it first");

            var probe = _probeService.Probe(magnetUrl);

            if (!string.IsNullOrEmpty(probe.Error))
            {
                throw new InvalidOperationException($"Real-Debrid could not add this magnet: {probe.Error}");
            }

            if (probe.Files == null || probe.Files.Count == 0)
            {
                if (probe.TimedOut)
                {
                    throw new InvalidOperationException("Timed out (45s) waiting for Real-Debrid to return this magnet's file list. RD may not have it cached yet — try again in a moment.");
                }

                throw new InvalidOperationException("Real-Debrid returned no files for this magnet.");
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
                var examples = string.Join("; ", probe.Files.Take(3).Select(f => Path.GetFileName(f.Path ?? string.Empty)));
                _logger.Warn("[SeasonSplit] Magnet preview: could not parse any season from {0} files (e.g. {1})", probe.Files.Count, examples);
                throw new InvalidOperationException($"Found {probe.Files.Count} files but couldn't determine season/episode numbers from their names (e.g. \"{examples}\"). The pack may use unusual naming.");
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
                    Indexer = IndexerName,
                });

                meta[guid] = new MagnetSeasonPreview { Season = season, Size = size, FileCount = files.Count, Title = title };
            }

            var decisions = _decisionMaker.GetRssDecision(synthetics, false);

            var result = new List<MagnetSeasonPreview>();

            foreach (var decision in decisions)
            {
                var guid = decision.RemoteEpisode?.Release?.Guid;

                if (guid == null || !meta.TryGetValue(guid, out var row))
                {
                    continue;
                }

                row.Decision = decision;

                var episodes = decision.RemoteEpisode?.Episodes ?? new List<Episode>();
                row.EpisodeCount = episodes.Count;
                row.ExistingCount = episodes.Count(e => e.HasFile);
                row.Satisfied = decision.Rejections?.Any(r => IsAlreadyHaveReason(r.Reason)) ?? false;

                // Default: hide seasons we already have (or are grabbing) at >= this
                // quality, so re-pasting the same magnet shows only what's still
                // worth getting. "Show everything" passes includeSatisfied=true.
                if (!includeSatisfied && row.Satisfied)
                {
                    continue;
                }

                result.Add(row);
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

            m = SeasonFromNumberX.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out s))
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
