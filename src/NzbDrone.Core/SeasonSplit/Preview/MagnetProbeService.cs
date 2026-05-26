using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.SeasonSplit.Preview
{
    public sealed class MagnetProbeFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
    }

    // The real file list + sizes for a magnet, read back from Real-Debrid via the
    // qBittorrent-compatible download client (rdt-client). A magnet itself carries
    // no size/file information, so the only way to show per-season sizes in a
    // preview is to briefly hand the magnet to the provider and read what it has.
    public sealed class MagnetProbeResult
    {
        public string Hash { get; set; }
        public string Name { get; set; }
        public long TotalSize { get; set; }
        public bool TimedOut { get; set; }

        // A permanent provider error surfaced by rdt-client (e.g. "Could not add
        // to provider: Infringing file"). When set, the probe failed fast and
        // Files is empty - the preview turns this into a clear message.
        public string Error { get; set; }

        public List<MagnetProbeFile> Files { get; set; } = new List<MagnetProbeFile>();
    }

    public interface IMagnetProbeService
    {
        MagnetProbeResult Probe(string magnetUrl);
    }

    public sealed class MagnetProbeService : IMagnetProbeService
    {
        private static readonly Regex BtihRegex =
            new Regex(@"xt=urn:btih:([A-Fa-f0-9]{40}|[A-Za-z2-7]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IQBittorrentProxySelector _proxySelector;
        private readonly Logger _logger;

        public MagnetProbeService(IProvideDownloadClient downloadClientProvider,
                                  IQBittorrentProxySelector proxySelector,
                                  Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _proxySelector = proxySelector;
            _logger = logger;
        }

        public MagnetProbeResult Probe(string magnetUrl)
        {
            if (string.IsNullOrWhiteSpace(magnetUrl))
            {
                throw new ArgumentException("A magnet link is required", nameof(magnetUrl));
            }

            var hash = ExtractHash(magnetUrl);
            if (string.IsNullOrEmpty(hash))
            {
                throw new InvalidOperationException("Could not parse an infohash from the magnet link");
            }

            hash = hash.ToLowerInvariant();

            var settings = ResolveQBittorrentSettings();
            var proxy = _proxySelector.GetProxy(settings);

            _logger.Info("[SeasonSplit] Probing magnet {0} for its file list via the download client", hash);

            // Hand the magnet to the provider so it fetches the torrent metadata.
            proxy.AddTorrentFromUrlWithExtras(magnetUrl, null, settings, new Dictionary<string, string>());

            try
            {
                var result = new MagnetProbeResult { Hash = hash };
                var deadline = DateTime.UtcNow.Add(ProbeTimeout);

                while (true)
                {
                    var torrent = proxy.GetTorrents(settings)
                                       .FirstOrDefault(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase));

                    if (torrent != null)
                    {
                        result.Name = torrent.Name;
                        result.TotalSize = torrent.Size;

                        // rdt-client reports a permanent provider failure here
                        // (e.g. "Could not add to provider: Infringing file").
                        // Fail fast — files will never appear, so don't burn the
                        // full 45s timeout waiting for them.
                        if (!string.IsNullOrWhiteSpace(torrent.RdtError))
                        {
                            _logger.Warn("[SeasonSplit] Probe for {0} failed: {1}", hash, torrent.RdtError);
                            result.Error = torrent.RdtError;
                            return result;
                        }
                    }

                    var files = proxy.GetTorrentFiles(hash, settings);

                    if (files is { Count: > 0 })
                    {
                        result.Files = files.Select(f => new MagnetProbeFile { Path = f.Name, Size = f.Size }).ToList();

                        if (result.TotalSize <= 0)
                        {
                            result.TotalSize = result.Files.Sum(f => f.Size);
                        }

                        _logger.Info("[SeasonSplit] Probe for {0} resolved {1} files, {2} bytes", hash, result.Files.Count, result.TotalSize);

                        return result;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        _logger.Warn("[SeasonSplit] Magnet probe for {0} timed out waiting for file metadata", hash);
                        result.TimedOut = true;
                        return result;
                    }

                    Thread.Sleep(2000);
                }
            }
            finally
            {
                // Always clean up the probe torrent — the user hasn't committed to
                // grabbing anything yet. RD dedups, so a later real grab re-adds
                // instantly.
                try
                {
                    proxy.RemoveTorrent(hash, true, settings);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[SeasonSplit] Failed to remove probe torrent {0} after preview", hash);
                }
            }
        }

        private QBittorrentSettings ResolveQBittorrentSettings()
        {
            foreach (var client in _downloadClientProvider.GetDownloadClients(DownloadProtocol.Torrent))
            {
                if (client.Definition?.Settings is QBittorrentSettings settings)
                {
                    return settings;
                }
            }

            throw new InvalidOperationException("No qBittorrent-compatible torrent download client is configured");
        }

        private static string ExtractHash(string magnet)
        {
            var m = BtihRegex.Match(magnet ?? string.Empty);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
