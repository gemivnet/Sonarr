using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
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

    // The real file list + sizes for a magnet, read back from the debrid provider via the
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
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public MagnetProbeService(IProvideDownloadClient downloadClientProvider,
                                  IQBittorrentProxySelector proxySelector,
                                  IHttpClient httpClient,
                                  Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _proxySelector = proxySelector;
            _httpClient = httpClient;
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

            // Preferred path: ask the provider for the file list WITHOUT adding a
            // download (TorBox /torrents/torrentinfo, proxied by rdt-client's
            // ssmetadata endpoint). Avoids the phantom download, rate-limit
            // hammering and 45s stall of the add-and-poll fallback. Returns null
            // only when the endpoint isn't available (older rdt-client) or the
            // provider isn't TorBox — then we fall back to add-and-poll below.
            var viaEndpoint = TryMetadataEndpoint(magnetUrl, settings, hash);
            if (viaEndpoint != null)
            {
                return viaEndpoint;
            }

            var proxy = _proxySelector.GetProxy(settings);

            _logger.Info("[SeasonSplit] Probing magnet {0} for its file list via the download client", hash);

            // Hand the magnet to the provider so it fetches the torrent metadata.
            proxy.AddTorrentFromUrl(magnetUrl, null, settings);

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
                // grabbing anything yet. The provider dedups, so a later real grab
                // re-adds instantly.
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

        // Ask rdt-client for the file list WITHOUT adding a download. Returns:
        //   - a populated result (files) on success,
        //   - a result with Error/TimedOut set when TorBox can't fetch metadata
        //     (we deliberately DON'T fall back to add-and-poll then, to avoid a
        //     phantom download), or
        //   - null when the endpoint is absent (404, old rdt-client) or the
        //     provider isn't TorBox (501) -> caller falls back to add-and-poll.
        private MagnetProbeResult TryMetadataEndpoint(string magnetUrl, QBittorrentSettings settings, string hash)
        {
            HttpResponse response;

            try
            {
                var scheme = settings.UseSsl ? "https" : "http";
                var url = $"{scheme}://{settings.Host}:{settings.Port}";

                var urlBase = settings.UrlBase?.Trim('/');
                if (!string.IsNullOrWhiteSpace(urlBase))
                {
                    url += "/" + urlBase;
                }

                url += "/api/v2/torrents/ssmetadata";

                var request = new HttpRequestBuilder(url).Post().AddFormParameter("magnet", magnetUrl).Build();
                request.RequestTimeout = TimeSpan.FromSeconds(70);
                request.SuppressHttpError = true;

                _logger.Info("[SeasonSplit] Fetching magnet {0} metadata via rdt-client ssmetadata (no download added)", hash);
                response = _httpClient.Post(request);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[SeasonSplit] ssmetadata call failed; falling back to the add-and-poll probe");
                return null;
            }

            var status = (int)response.StatusCode;

            // 404 (older rdt-client without the endpoint) or 501 (provider isn't
            // TorBox) -> fall back to the add-and-poll probe.
            if (status == 404 || status == 501)
            {
                return null;
            }

            if (status == 200)
            {
                SsMetadataResponse parsed;

                try
                {
                    parsed = Json.Deserialize<SsMetadataResponse>(response.Content);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[SeasonSplit] could not parse ssmetadata response; falling back");
                    return null;
                }

                var files = (parsed?.Files ?? new List<SsMetadataFileResponse>())
                    .Where(f => !string.IsNullOrEmpty(f.Name))
                    .Select(f => new MagnetProbeFile { Path = f.Name, Size = f.Size })
                    .ToList();

                if (files.Count > 0)
                {
                    _logger.Info("[SeasonSplit] ssmetadata resolved {0} files for {1} (no download created)", files.Count, hash);
                    return new MagnetProbeResult { Hash = hash, Name = parsed?.Name, Files = files, TotalSize = files.Sum(f => f.Size) };
                }

                return new MagnetProbeResult { Hash = hash, Error = "TorBox returned no files for this magnet." };
            }

            // TorBox couldn't fetch metadata (502) or timed out (504). Surface a
            // clean, retryable message - the first lookup fetches from the network
            // and caches it, so a retry usually resolves instantly.
            _logger.Warn("[SeasonSplit] ssmetadata returned {0} for {1}", status, hash);
            return new MagnetProbeResult
            {
                Hash = hash,
                TimedOut = status == 504,
                Error = "TorBox couldn't fetch this torrent's metadata yet (it may be low-seed). Try Preview again in a moment, or use a different release.",
            };
        }

        private static string ExtractHash(string magnet)
        {
            var m = BtihRegex.Match(magnet ?? string.Empty);
            return m.Success ? m.Groups[1].Value : null;
        }

        private sealed class SsMetadataResponse
        {
            public string Name { get; set; }
            public List<SsMetadataFileResponse> Files { get; set; }
        }

        private sealed class SsMetadataFileResponse
        {
            public string Name { get; set; }
            public long Size { get; set; }
        }
    }
}
