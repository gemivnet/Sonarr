using System;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.AutoBlocklist
{
    // Shared "auto-blocklist this download" action for the watchers. Mirrors the
    // manual queue "Remove + Blocklist + Search" flow (QueueController.Remove):
    // remove the item from the download client first (Sonarr won't auto-remove an
    // error/warning-state torrent on its own), then mark it failed so it's
    // blocklisted and re-searched.
    internal static class AutoBlocklistActions
    {
        public static void FailAndRemove(IProvideDownloadClient downloadClientProvider,
                                         IFailedDownloadService failedDownloadService,
                                         TrackedDownload trackedDownload,
                                         string message,
                                         Logger logger)
        {
            var item = trackedDownload.DownloadItem;

            try
            {
                var downloadClient = downloadClientProvider.Get(trackedDownload.DownloadClient);
                downloadClient?.RemoveItem(item, true);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[AutoBlocklist] Failed to remove {0} from the download client", item?.Title);
            }

            try
            {
                failedDownloadService.MarkAsFailed(trackedDownload, message);
            }
            catch (InvalidOperationException)
            {
                // MarkAsFailed always throws after (conditionally) publishing the
                // DownloadFailedEvent — this is upstream behaviour, not an error.
                // When grabbed history existed the blocklist + re-search already
                // fired; when it didn't there was nothing to blocklist anyway.
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[AutoBlocklist] Failed to mark {0} as failed", item?.Title);
            }
        }
    }
}
