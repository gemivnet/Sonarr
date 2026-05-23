using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.SeasonSplit;

namespace NzbDrone.Core.Indexers
{
    public class RssSyncService : IExecute<RssSyncCommand>
    {
        private readonly IFetchAndParseRss _rssFetcherAndParser;
        private readonly IMakeDownloadDecision _downloadDecisionMaker;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly ISeasonSplitReleaseExpander _seasonSplitExpander;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public RssSyncService(IFetchAndParseRss rssFetcherAndParser,
                              IMakeDownloadDecision downloadDecisionMaker,
                              IProcessDownloadDecisions processDownloadDecisions,
                              IPendingReleaseService pendingReleaseService,
                              ISeasonSplitReleaseExpander seasonSplitExpander,
                              IEventAggregator eventAggregator,
                              Logger logger)
        {
            _rssFetcherAndParser = rssFetcherAndParser;
            _downloadDecisionMaker = downloadDecisionMaker;
            _processDownloadDecisions = processDownloadDecisions;
            _pendingReleaseService = pendingReleaseService;
            _seasonSplitExpander = seasonSplitExpander;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private async Task<ProcessedDecisions> Sync()
        {
            _logger.ProgressInfo("Starting RSS Sync");

            var rssReleases = await _rssFetcherAndParser.Fetch();
            var pendingReleases = _pendingReleaseService.GetPending();

            var reports = rssReleases.Concat(pendingReleases).ToList();

            // SeasonSplit: RSS uses its own pipeline (it never touches
            // ReleaseSearchService, where search-time expansion lives), so
            // season packs arriving via the feed would otherwise pass through
            // un-split. Expand here too — no wanted-seasons scope on RSS, so
            // every season of a detected pack becomes a synthetic release.
            reports = _seasonSplitExpander.Expand(reports).ToList();

            var decisions = _downloadDecisionMaker.GetRssDecision(reports);
            var processed = await _processDownloadDecisions.ProcessDecisions(decisions);

            if (processed.Pending.Any())
            {
                _logger.ProgressInfo("RSS Sync Completed. Reports found: {0}, Reports grabbed: {1}, Reports pending: {2}", reports.Count, processed.Grabbed.Count, processed.Pending.Count);
            }
            else
            {
                _logger.ProgressInfo("RSS Sync Completed. Reports found: {0}, Reports grabbed: {1}", reports.Count, processed.Grabbed.Count);
            }

            return processed;
        }

        public void Execute(RssSyncCommand message)
        {
            var processed = Sync().GetAwaiter().GetResult();

            _eventAggregator.PublishEvent(new RssSyncCompleteEvent(processed));
        }
    }
}
