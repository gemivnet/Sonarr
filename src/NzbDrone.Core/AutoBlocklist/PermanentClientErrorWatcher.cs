using System;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AutoBlocklist
{
    // Auto-blocklist any tracked download whose client surfaces a permanent
    // failure marker (Real-Debrid 451/403/404 etc). Subscribes to the same
    // refresh event Sonarr already uses to drive its queue UI.
    public sealed class PermanentClientErrorWatcher : IHandle<TrackedDownloadRefreshedEvent>
    {
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly Logger _logger;

        public PermanentClientErrorWatcher(IFailedDownloadService failedDownloadService, Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _logger = logger;
            _logger.Info("[AutoBlocklist] PermanentClientErrorWatcher initialised (markers: {0})",
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
                var msg = td?.DownloadItem?.Message;
                if (string.IsNullOrEmpty(msg))
                {
                    continue;
                }

                if (!IsPermanent(msg))
                {
                    continue;
                }

                try
                {
                    _logger.Warn("[AutoBlocklist] Permanent client error on {0}: {1} — marking failed", td.DownloadItem.Title, msg);
                    _failedDownloadService.MarkAsFailed(td);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Auto-blocklist: failed to mark {0} as failed", td.DownloadItem.Title);
                }
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

            return false;
        }
    }
}
