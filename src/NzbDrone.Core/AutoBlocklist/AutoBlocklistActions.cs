using System;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.SeasonSplit.Preview;

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

            // Manual "Add Magnet" grabs are a deliberate one-off the user chose by
            // pasting a specific magnet. Don't blocklist them or auto-search a
            // replacement (that just churns through other - frequently also
            // infringing - releases). Leave the errored item visible in the queue
            // so the user can deal with it by hand. Automated RSS/search grabs
            // keep the normal blocklist + re-search behaviour.
            if (string.Equals(trackedDownload.Indexer, MagnetPreviewService.IndexerName, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info("[AutoBlocklist] Leaving manual Add Magnet grab '{0}' as-is (no blocklist / re-search): {1}", item?.Title, message);
                return;
            }

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
