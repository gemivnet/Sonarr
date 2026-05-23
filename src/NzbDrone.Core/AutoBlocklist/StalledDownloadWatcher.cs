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
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new ConcurrentDictionary<string, Snapshot>();

        // DownloadIds already marked failed. MarkAsFailed publishes its event and
        // then throws, so cleanup placed after the call never runs — without this
        // guard a lingering stalled item would be re-failed (re-blocklisted +
        // re-searched) on every refresh.
        private readonly ConcurrentDictionary<string, byte> _processed = new ConcurrentDictionary<string, byte>();

        public StalledDownloadWatcher(IFailedDownloadService failedDownloadService, IProvideDownloadClient downloadClientProvider, Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _downloadClientProvider = downloadClientProvider;
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

                // Only an actively-Downloading item can "stall". A merely-Queued
                // item legitimately makes no progress (it hasn't started), so
                // blocklisting it after the threshold would be a false positive.
                if (item.Status != DownloadItemStatus.Downloading)
                {
                    _snapshots.TryRemove(item.DownloadId, out _);
                    continue;
                }

                var snap = _snapshots.GetOrAdd(item.DownloadId, _ => new Snapshot { RemainingSize = item.RemainingSize, ObservedAtUtc = now });

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

                // Record before the call (MarkAsFailed throws after publishing).
                if (!_processed.TryAdd(item.DownloadId, 0))
                {
                    continue;
                }

                _snapshots.TryRemove(item.DownloadId, out _);

                _logger.Warn("[AutoBlocklist] Download stalled for {0}h on {1} — removing, blocklisting and re-searching", AutoBlocklistConfig.StallThresholdHours, item.Title);
                AutoBlocklistActions.FailAndRemove(_downloadClientProvider, _failedDownloadService, td, $"Stalled with no progress for {AutoBlocklistConfig.StallThresholdHours}h", _logger);
            }
        }
    }
}
