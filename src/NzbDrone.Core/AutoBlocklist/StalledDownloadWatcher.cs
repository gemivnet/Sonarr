using System;
using System.Collections.Concurrent;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AutoBlocklist
{
    // Tracks per-DownloadId remaining-size snapshots. If a download's
    // remaining size hasn't moved for StallThresholdHours, blocklist it.
    public sealed class StalledDownloadWatcher : IHandle<TrackedDownloadRefreshedEvent>
    {
        private sealed class Snapshot
        {
            public long RemainingSize;
            public DateTime ObservedAtUtc;
        }

        private readonly IFailedDownloadService _failedDownloadService;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new();

        public StalledDownloadWatcher(IFailedDownloadService failedDownloadService, Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _logger = logger;
            _logger.Info("[AutoBlocklist] StalledDownloadWatcher initialised (threshold: {0}h)", AutoBlocklistConfig.StallThresholdHours);
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            if (!AutoBlocklistConfig.Enabled || message?.TrackedDownloads == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var threshold = TimeSpan.FromHours(AutoBlocklistConfig.StallThresholdHours);

            foreach (var td in message.TrackedDownloads)
            {
                var item = td?.DownloadItem;
                if (item == null || string.IsNullOrEmpty(item.DownloadId))
                {
                    continue;
                }

                if (item.Status != DownloadItemStatus.Downloading && item.Status != DownloadItemStatus.Queued)
                {
                    _snapshots.TryRemove(item.DownloadId, out _);
                    continue;
                }

                var snap = _snapshots.GetOrAdd(item.DownloadId, _ => new Snapshot
                {
                    RemainingSize = item.RemainingSize,
                    ObservedAtUtc = now,
                });

                if (snap.RemainingSize != item.RemainingSize)
                {
                    snap.RemainingSize = item.RemainingSize;
                    snap.ObservedAtUtc = now;
                    continue;
                }

                if (now - snap.ObservedAtUtc < threshold)
                {
                    continue;
                }

                try
                {
                    _logger.Warn("Auto-blocklist: download stalled for {0}h on {1}",
                        AutoBlocklistConfig.StallThresholdHours, item.Title);
                    _failedDownloadService.MarkAsFailed(td, "Download stalled — no progress for configured threshold", source: "AutoBlocklist");
                    _snapshots.TryRemove(item.DownloadId, out _);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Auto-blocklist: failed to mark stalled {0} as failed", item.Title);
                }
            }
        }
    }
}
