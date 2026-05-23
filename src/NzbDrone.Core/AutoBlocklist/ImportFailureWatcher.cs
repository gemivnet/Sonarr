using System.Collections.Concurrent;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AutoBlocklist
{
    // Counts EpisodeImportFailedEvent occurrences per DownloadId. After
    // MaxImportRetries, surface the tracked download (via the next refresh)
    // and blocklist it.
    public sealed class ImportFailureWatcher :
        IHandle<EpisodeImportFailedEvent>,
        IHandle<TrackedDownloadRefreshedEvent>
    {
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, int> _failureCounts = new ConcurrentDictionary<string, int>();

        public ImportFailureWatcher(IFailedDownloadService failedDownloadService, IProvideDownloadClient downloadClientProvider, Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _logger = logger;
            _logger.Info("[AutoBlocklist] ImportFailureWatcher initialised (max retries: {0})", AutoBlocklistConfig.MaxImportRetries);
        }

        public void Handle(EpisodeImportFailedEvent message)
        {
            if (!AutoBlocklistConfig.Enabled || string.IsNullOrEmpty(message?.DownloadId))
            {
                return;
            }

            var count = _failureCounts.AddOrUpdate(message.DownloadId, 1, (_, v) => v + 1);
            _logger.Debug("[AutoBlocklist] Import failure #{0} for download {1}", count, message.DownloadId);
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            if (!AutoBlocklistConfig.Enabled || message?.TrackedDownloads == null)
            {
                return;
            }

            foreach (var td in message.TrackedDownloads)
            {
                var id = td?.DownloadItem?.DownloadId;
                if (string.IsNullOrEmpty(id) || !_failureCounts.TryGetValue(id, out var count))
                {
                    continue;
                }

                if (count < AutoBlocklistConfig.MaxImportRetries)
                {
                    continue;
                }

                // Drop the counter before the call: MarkAsFailed publishes its
                // event and then throws, so a TryRemove placed after it never
                // runs and the item would be re-failed on every refresh.
                _failureCounts.TryRemove(id, out _);

                _logger.Warn("[AutoBlocklist] {0} import failures on {1} — removing, blocklisting and re-searching", count, td.DownloadItem.Title);
                AutoBlocklistActions.FailAndRemove(_downloadClientProvider, _failedDownloadService, td, $"Repeated import failures ({count})", _logger);
            }
        }
    }
}
