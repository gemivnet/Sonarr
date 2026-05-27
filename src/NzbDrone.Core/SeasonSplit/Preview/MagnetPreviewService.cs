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
    // One episode within a season, mapped from a file in the pack. Drives the
    // expandable per-episode breakdown and the partial-season checkbox state.
    public sealed class MagnetEpisodePreview
    {
        public int Episode { get; set; }
        public string Title { get; set; }
        public long Size { get; set; }
        public string Quality { get; set; }
        public bool HasFile { get; set; }
    }

    public sealed class MagnetSeasonPreview
    {
        public int Season { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        public string Title { get; set; }
        public int EpisodeCount { get; set; }
        public int ExistingCount { get; set; }
        public bool Satisfied { get; set; }
        public List<MagnetEpisodePreview> Episodes { get; set; } = new List<MagnetEpisodePreview>();
        public DownloadDecision Decision { get; set; }
    }

    // A single episode the user ticked for a per-episode grab.
    public sealed class MagnetEpisodeSelection
    {
        public int Season { get; set; }
        public int Episode { get; set; }
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
        MagnetGrabResult Grab(string magnetUrl, int tvdbId, IReadOnlyCollection<int> seasons, IReadOnlyCollection<MagnetEpisodeSelection> episodes, int? downloadClientId);
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

        private static readonly Regex BtihRegex = new Regex(@"xt=urn:btih:([A-Fa-f0-9]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // S##E## with an optional separator between the two (S01E01, S01.E01,
        // S01 E01, S01-E01 — the IT Crowd DVD pack uses the dotted form).
        private static readonly Regex SeasonFromName = new Regex(@"(?i)\bS(\d{1,2})[ ._-]?E\d{1,3}\b", RegexOptions.Compiled);

        // "NxNN" numbering (e.g. "07x03", "1x02") - common on non-English packs
        // (the Anthony Bourdain CZ pack uses it). The leading word boundary keeps
        // it off mid-number matches like resolutions ("1920x1080").
        private static readonly Regex SeasonFromNumberX = new Regex(@"(?i)\b(\d{1,2})x\d{2,3}\b", RegexOptions.Compiled);

        private static readonly Regex SeasonFromFolder = new Regex(@"(?i)(?:^|[/\\])Season[\s._-]*(\d{1,2})(?:[/\\]|$)", RegexOptions.Compiled);

        // Season + episode together, for the per-episode breakdown. SxxExx and the
        // NxNN form (07x03). Files matched only by folder ("Season 06/name.mkv")
        // have no episode marker and so don't get a per-episode row.
        private static readonly Regex EpisodeFromName = new Regex(@"(?i)\bS(\d{1,2})[ ._-]?E(\d{1,3})\b", RegexOptions.Compiled);
        private static readonly Regex EpisodeFromNumberX = new Regex(@"(?i)\b(\d{1,2})x(\d{2,3})\b", RegexOptions.Compiled);
        private static readonly Regex ResolutionToken = new Regex(@"(?i)\b(2160p|1080p|720p|576p|480p|360p)\b", RegexOptions.Compiled);

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

        // "We already have this (or are actively grabbing it) at >= the quality
        // this release offers." Disk = a file on disk wins; Queue = a download is
        // in flight; AlreadyImported = imported. Deliberately NOT History: a
        // "recent grab event in history" fires even for grabs that FAILED (e.g.
        // Real-Debrid infringing), so treating it as "have it" would wrongly hide
        // seasons that never actually downloaded. Library presence is judged
        // separately (ExistingCount), which is the source of truth.
        private static bool IsAlreadyHaveReason(DownloadRejectionReason reason)
        {
            var name = reason.ToString();

            return name.StartsWith("Disk", StringComparison.Ordinal) ||
                   name.StartsWith("Queue", StringComparison.Ordinal) ||
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

        // Grab the user's selection from the pack as ONE synthetic release PER
        // season, so each season is its own queue item (matching the
        // search/expander path's per-season siblings). Every sibling resolves to
        // the SAME real magnet — rdt-client coordinates them by a per-season
        // include regex, downloading only that season's files into each. Whole
        // seasons take all their episodes; partially-ticked seasons grab just the
        // chosen episodes. The user explicitly ticked these, so soft rejections
        // (quality not in profile, not an upgrade, already have) are overridden -
        // but a whole season still respects its size limit, the one hard ceiling.
        public MagnetGrabResult Grab(string magnetUrl, int tvdbId, IReadOnlyCollection<int> seasons, IReadOnlyCollection<MagnetEpisodeSelection> episodes, int? downloadClientId)
        {
            var result = new MagnetGrabResult();
            var wantedSeasons = seasons == null ? new HashSet<int>() : new HashSet<int>(seasons);
            var wantedEpisodes = new HashSet<(int Season, int Episode)>((episodes ?? Enumerable.Empty<MagnetEpisodeSelection>()).Select(e => (e.Season, e.Episode)));

            if (wantedSeasons.Count == 0 && wantedEpisodes.Count == 0)
            {
                return result;
            }

            // includeSatisfied: true so anything the user explicitly ticked can
            // still be resolved/grabbed even if the default preview would hide it.
            var bySeason = Preview(magnetUrl, tvdbId, includeSatisfied: true).ToDictionary(p => p.Season);

            // One grab unit per season: whole-season units carry the whole season
            // regex, partial units carry the exact episodes chosen for that season.
            var wholeSeasonGrabs = new List<(int Season, RemoteEpisode Anchor, List<Episode> Episodes)>();

            // Whole seasons: enforce the size ceiling, then take all their episodes.
            foreach (var s in wantedSeasons.OrderBy(x => x))
            {
                if (!bySeason.TryGetValue(s, out var preview) || preview.Decision?.RemoteEpisode == null)
                {
                    result.Skipped.Add(new MagnetGrabSkip { Season = s, Reason = "Could not resolve the release for this season" });
                    continue;
                }

                var sizeRejection = preview.Decision.Rejections?.FirstOrDefault(r => SizeReasons.Contains(r.Reason));

                if (sizeRejection != null)
                {
                    _logger.Info("[SeasonSplit] Add Magnet: refusing S{0:D2} on size limit: {1}", s, sizeRejection.Message);
                    result.Skipped.Add(new MagnetGrabSkip { Season = s, Reason = sizeRejection.Message });
                    continue;
                }

                wholeSeasonGrabs.Add((s, preview.Decision.RemoteEpisode, preview.Decision.RemoteEpisode.Episodes.ToList()));
            }

            var wholeSeasonSet = new HashSet<int>(wholeSeasonGrabs.Select(x => x.Season));

            // Individual episodes from partially-selected seasons, grouped by
            // season so each partial season is still a single queue item.
            var partialBySeason = new Dictionary<int, (RemoteEpisode Anchor, List<Episode> Episodes)>();

            foreach (var (s, e) in wantedEpisodes)
            {
                if (wholeSeasonSet.Contains(s))
                {
                    continue;
                }

                if (!bySeason.TryGetValue(s, out var preview) || preview.Decision?.RemoteEpisode == null)
                {
                    continue;
                }

                var ep = preview.Decision.RemoteEpisode.Episodes.FirstOrDefault(x => x.SeasonNumber == s && x.EpisodeNumber == e);

                if (ep == null)
                {
                    continue;
                }

                if (!partialBySeason.TryGetValue(s, out var entry))
                {
                    entry = (preview.Decision.RemoteEpisode, new List<Episode>());
                    partialBySeason[s] = entry;
                }

                entry.Episodes.Add(ep);
            }

            // Emit one synthetic release + grab per whole season.
            foreach (var (s, anchor, eps) in wholeSeasonGrabs)
            {
                var release = BuildSeasonGrab(magnetUrl, tvdbId, anchor, eps, new List<int> { s }, new List<(int Season, int Episode)>());
                _logger.Info("[SeasonSplit] Add Magnet: grabbing S{0:D2} ({1} episodes) as its own torrent", s, eps.Count);
                _downloadService.DownloadReport(release, downloadClientId).GetAwaiter().GetResult();
                result.Grabbed.Add(s);
            }

            // Emit one synthetic release + grab per partially-selected season.
            foreach (var kv in partialBySeason.OrderBy(x => x.Key))
            {
                var s = kv.Key;
                var (anchor, eps) = kv.Value;
                var grabEpisodes = eps.Select(e => (e.SeasonNumber, e.EpisodeNumber)).ToList();
                var release = BuildSeasonGrab(magnetUrl, tvdbId, anchor, eps, new List<int>(), grabEpisodes);
                _logger.Info("[SeasonSplit] Add Magnet: grabbing S{0:D2} episodes [{1}] as its own torrent", s, string.Join(",", grabEpisodes.Select(x => $"E{x.Item2:D2}")));
                _downloadService.DownloadReport(release, downloadClientId).GetAwaiter().GetResult();
                result.Grabbed.Add(s);
            }

            return result;
        }

        // Build one synthetic release for a single season's slice of the pack
        // (either the whole season, or a set of episodes from it), with an explicit
        // per-season include regex carried on the magnet as x.includeregex for the
        // download client. Identity is keyed by the slice so each season's grab is
        // a deterministic, distinct queue item that still points at the one real
        // magnet (rdt-client coordinates the siblings).
        private RemoteEpisode BuildSeasonGrab(string magnetUrl, int tvdbId, RemoteEpisode anchor, List<Episode> episodesToReport, List<int> grabSeasons, List<(int Season, int Episode)> grabEpisodes)
        {
            var episodes = episodesToReport
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();

            var realHash = ExtractHash(magnetUrl);
            var key = "sel:" + string.Join(",", grabSeasons.OrderBy(x => x)) + "|" + string.Join(",", grabEpisodes.OrderBy(x => x.Season).ThenBy(x => x.Episode).Select(x => $"{x.Season}x{x.Episode}"));

            var includeRegex = SeasonSplitIncludeRegex.Build(grabSeasons, grabEpisodes);
            var magnetWithRegex = includeRegex == null
                ? magnetUrl
                : magnetUrl + "&x.includeregex=" + Uri.EscapeDataString(includeRegex);

            var release = new TorrentInfo
            {
                Guid = _detector.SyntheticGuid(realHash, key),
                Title = BuildGrabTitle(anchor.Series.Title, grabSeasons, grabEpisodes),
                Size = 0,
                MagnetUrl = magnetWithRegex,
                DownloadUrl = magnetWithRegex,
                InfoHash = _detector.SyntheticInfohash(realHash, key),
                TvdbId = tvdbId,
                DownloadProtocol = DownloadProtocol.Torrent,
                PublishDate = DateTime.UtcNow,
                Indexer = IndexerName,
            };

            return new RemoteEpisode
            {
                Release = release,
                Series = anchor.Series,
                Episodes = episodes,
                ParsedEpisodeInfo = anchor.ParsedEpisodeInfo,
            };
        }

        private static string BuildGrabTitle(string seriesTitle, List<int> seasons, List<(int Season, int Episode)> episodes)
        {
            var parts = new List<string>();

            if (seasons.Count > 0)
            {
                var ordered = seasons.OrderBy(x => x).ToList();
                parts.Add(ordered.Count == 1 ? $"S{ordered[0]:D2}" : $"S{ordered.First():D2}-S{ordered.Last():D2}");
            }

            foreach (var (s, e) in episodes.OrderBy(x => x.Season).ThenBy(x => x.Episode))
            {
                parts.Add($"S{s:D2}E{e:D2}");
            }

            return $"{seriesTitle} {string.Join(" ", parts)}".Trim();
        }

        private static string ExtractHash(string magnetUrl)
        {
            var m = BtihRegex.Match(magnetUrl ?? string.Empty);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : string.Empty;
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
            var epFilesBySeason = new Dictionary<int, List<(int Episode, MagnetProbeFile File)>>();

            foreach (var file in probe.Files)
            {
                if (!IsVideoFile(file.Path))
                {
                    continue;
                }

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

                // Per-episode breakdown: record the file under its episode number
                // when we can read one (SxxExx / NxNN). Folder-only matches don't
                // get an episode row.
                var se = EpisodeOf(file.Path);
                if (se != null && se.Value.Season == season.Value)
                {
                    if (!epFilesBySeason.TryGetValue(season.Value, out var epList))
                    {
                        epList = new List<(int, MagnetProbeFile)>();
                        epFilesBySeason[season.Value] = epList;
                    }

                    epList.Add((se.Value.Episode, file));
                }
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

                // Satisfied = "don't bother showing by default": the season is
                // rejected AND we genuinely have it (every episode on disk, or a
                // disk/queue/imported rejection). A recent grab that FAILED leaves
                // the episodes missing, so it stays visible.
                var fullyInLibrary = row.EpisodeCount > 0 && row.ExistingCount >= row.EpisodeCount;
                var haveOrInFlight = decision.Rejections?.Any(r => IsAlreadyHaveReason(r.Reason)) ?? false;
                row.Satisfied = !decision.Approved && (fullyInLibrary || haveOrInFlight);

                // Per-episode rows: one per episode number found in the pack's
                // files, cross-referenced with the season's episodes for the
                // in-library flag + title.
                if (epFilesBySeason.TryGetValue(row.Season, out var epFiles))
                {
                    var epByNumber = episodes
                        .GroupBy(e => e.EpisodeNumber)
                        .ToDictionary(g => g.Key, g => g.First());

                    row.Episodes = epFiles
                        .GroupBy(x => x.Episode)
                        .Select(g =>
                        {
                            epByNumber.TryGetValue(g.Key, out var ep);
                            var largest = g.OrderByDescending(x => x.File.Size).First().File;

                            return new MagnetEpisodePreview
                            {
                                Episode = g.Key,
                                Title = ep?.Title,
                                Size = g.Sum(x => x.File.Size),
                                Quality = FileResolution(largest.Path),
                                HasFile = ep?.HasFile ?? false,
                            };
                        })
                        .OrderBy(e => e.Episode)
                        .ToList();
                }

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
            var rep = files.Where(f => IsVideoFile(f.Path))
                           .OrderByDescending(f => f.Size)
                           .FirstOrDefault() ?? files[0];

            // Search the whole relative path (folder + filename) for quality
            // tokens, not just the filename - packs often put the quality on the
            // folder ("WLIIA S20 (360p re-tvrip)/...") while the file name carries
            // none. Appending the distinct tokens makes the synthetic title parse
            // to the same quality Sonarr reads off the file at import, so the
            // queue/history quality matches the library instead of showing Unknown.
            var searchText = (rep.Path ?? string.Empty).Replace('/', ' ').Replace('\\', ' ');
            var tokens = QualityToken.Matches(searchText)
                                     .Select(m => m.Value)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToList();
            var quality = string.Join(" ", tokens);

            var title = $"{seriesTitle} S{season:D2} {quality}".Trim();

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

        private static (int Season, int Episode)? EpisodeOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var m = EpisodeFromName.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s) && int.TryParse(m.Groups[2].Value, out var e))
            {
                return (s, e);
            }

            m = EpisodeFromNumberX.Match(path);
            if (m.Success && int.TryParse(m.Groups[1].Value, out s) && int.TryParse(m.Groups[2].Value, out e))
            {
                return (s, e);
            }

            return null;
        }

        private static string FileResolution(string path)
        {
            var m = ResolutionToken.Match(Path.GetFileName(path ?? string.Empty));
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool IsVideoFile(string path)
        {
            var ext = Path.GetExtension(path ?? string.Empty)?.ToLowerInvariant() ?? string.Empty;
            return VideoExts.Contains(ext);
        }
    }
}
