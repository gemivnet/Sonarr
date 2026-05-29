using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AutoBlocklist
{
    // Auto-blocklist any tracked download whose client surfaces a permanent
    // failure marker (debrid-provider infringing_file / HTTP 403/404/451 etc).
    // Subscribes to the same refresh event Sonarr already uses to drive its
    // queue UI.
    public sealed class PermanentClientErrorWatcher : IHandle<TrackedDownloadRefreshedEvent>
    {
        private static readonly Regex CodeRegex =
            new Regex(AutoBlocklistConfig.PermanentErrorCodePattern, RegexOptions.Compiled);

        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly Logger _logger;

        // DownloadIds we've already marked failed. A permanently-errored item
        // typically lingers in the queue with the same message across refreshes
        // (especially when the client's RemoveFailedDownloads is off), so
        // without this guard we'd re-publish DownloadFailedEvent — and re-trigger
        // blocklist + re-search — on every refresh.
        private readonly ConcurrentDictionary<string, byte> _processed = new ConcurrentDictionary<string, byte>();

        public PermanentClientErrorWatcher(IFailedDownloadService failedDownloadService, IProvideDownloadClient downloadClientProvider, Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _logger = logger;
            _logger.Info("[AutoBlocklist] PermanentClientErrorWatcher initialised (markers: {0}; codes via context-anchored regex)",
                string.Join(", ", AutoBlocklistConfig.PermanentErrorMarkers));
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            if (!AutoBlocklistConfig.Enabled || message?.TrackedDownloads == null)
            {
                return;
            }

            foreach (var td in message.TrackedDownloads)
            {
                var item = td?.DownloadItem;
                if (item == null || string.IsNullOrEmpty(item.DownloadId))
                {
                    continue;
                }

                // Only consider items the client is actually flagging as a
                // problem. Actively-progressing (Queued/Downloading) and paused
                // items are never permanent failures, so skip them — this keeps
                // a benign in-progress Message out of the matcher entirely.
                if (item.Status != DownloadItemStatus.Warning && item.Status != DownloadItemStatus.Failed)
                {
                    continue;
                }

                var msg = item.Message;
                if (string.IsNullOrEmpty(msg) || !IsPermanent(msg))
                {
                    continue;
                }

                // Dedup: only act once per DownloadId. Record before the call —
                // MarkAsFailed publishes its event and then throws, so doing it
                // after would never run.
                if (!_processed.TryAdd(item.DownloadId, 0))
                {
                    continue;
                }

                _logger.Warn("[AutoBlocklist] Permanent client error on {0}: {1} — removing, blocklisting and re-searching", item.Title, msg);
                AutoBlocklistActions.FailAndRemove(_downloadClientProvider, _failedDownloadService, td, $"Permanent client error: {msg}", _logger);
            }
        }

        private static bool IsPermanent(string message)
        {
            foreach (var marker in AutoBlocklistConfig.PermanentErrorMarkers)
            {
                if (message.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return CodeRegex.IsMatch(message);
        }
    }
}
