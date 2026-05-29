using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.SeasonSplit;
using Sonarr.Http;
using Sonarr.Http.REST;

namespace Sonarr.Api.V5.Release;

[V5ApiController("release/push")]
public class ReleasePushController : RestController<ReleasePushResource>
{
    private readonly IMakeDownloadDecision _downloadDecisionMaker;
    private readonly IProcessDownloadDecisions _downloadDecisionProcessor;
    private readonly IIndexerFactory _indexerFactory;
    private readonly IDownloadClientFactory _downloadClientFactory;
    private readonly ISeasonSplitReleaseExpander _seasonSplitExpander;
    private readonly Logger _logger;

    private readonly QualityProfile _qualityProfile;

    private static readonly object PushLock = new object();

    public ReleasePushController(IMakeDownloadDecision downloadDecisionMaker,
                             IProcessDownloadDecisions downloadDecisionProcessor,
                             IIndexerFactory indexerFactory,
                             IDownloadClientFactory downloadClientFactory,
                             IQualityProfileService qualityProfileService,
                             ISeasonSplitReleaseExpander seasonSplitExpander,
                             Logger logger)
    {
        _downloadDecisionMaker = downloadDecisionMaker;
        _downloadDecisionProcessor = downloadDecisionProcessor;
        _indexerFactory = indexerFactory;
        _downloadClientFactory = downloadClientFactory;
        _seasonSplitExpander = seasonSplitExpander;
        _logger = logger;

        _qualityProfile = qualityProfileService.GetDefaultProfile(string.Empty);

        PostValidator.RuleFor(s => s.Title).NotEmpty();
        PostValidator.RuleFor(s => s.DownloadUrl).NotEmpty().When(s => s.MagnetUrl.IsNullOrWhiteSpace());
        PostValidator.RuleFor(s => s.MagnetUrl).NotEmpty().When(s => s.DownloadUrl.IsNullOrWhiteSpace());
        PostValidator.RuleFor(s => s.Protocol).NotEmpty();
        PostValidator.RuleFor(s => s.PublishDate).NotEmpty();
    }

    [HttpPost]
    [Consumes("application/json")]
    public Results<Ok<ReleaseResource>, BadRequest> Create([FromBody] ReleasePushResource release)
    {
        _logger.Info("Release pushed: {0} - {1}", release.Title, release.DownloadUrl ?? release.MagnetUrl);

        ValidateResource(release);

        var info = release.ToModel();

        info.Guid = "PUSH-" + info.DownloadUrl;

        ResolveIndexer(info);

        var downloadClientId = ResolveDownloadClientId(release);

        // Season-split: run the pushed release through the same expander that
        // search/RSS use. A hand-fed multi-season magnet ("Show S01-S05") is
        // cloned into per-season synthetic releases and grabbed one season at a
        // time through the debrid provider; a normal single release is returned as-is
        // (Expand is a no-op), so ordinary pushes behave exactly as before.
        // TvdbId (set by the caller) is stamped onto the synthetics so they map
        // to the right series even when the pack title doesn't cleanly parse.
        var releases = _seasonSplitExpander.Expand(new List<ReleaseInfo> { info }, null, info.TvdbId).ToList();
        var wasSplit = releases.Count > 1;

        List<DownloadDecision> decisions;

        lock (PushLock)
        {
            decisions = _downloadDecisionMaker.GetRssDecision(releases, true).ToList();

            if (wasSplit)
            {
                // Grab each approved per-season clone; the raw multi-season pack
                // is rejected ("multi-season not supported") and skipped.
                foreach (var d in decisions.Where(d => d.Approved))
                {
                    _downloadDecisionProcessor.ProcessDecision(d, downloadClientId).GetAwaiter().GetResult();
                }
            }
            else
            {
                _downloadDecisionProcessor.ProcessDecision(decisions.FirstOrDefault(), downloadClientId).GetAwaiter().GetResult();
            }
        }

        // Report an approved decision when there is one, otherwise the first that
        // at least parsed, so the caller gets a meaningful resource back.
        var primary = decisions.FirstOrDefault(d => d.Approved)
                      ?? decisions.FirstOrDefault(d => d.RemoteEpisode?.ParsedEpisodeInfo != null);

        if (primary?.RemoteEpisode?.ParsedEpisodeInfo == null)
        {
            throw new ValidationException(new List<ValidationFailure> { new("Title", "Unable to parse", release.Title) });
        }

        _logger.Info("Release push processed: {0} -> {1} release(s), {2} approved{3}", release.Title, releases.Count, decisions.Count(d => d.Approved), wasSplit ? " (season-split)" : "");

        return TypedResults.Ok(primary.MapDecision(1, _qualityProfile));
    }

    private void ResolveIndexer(ReleaseInfo release)
    {
        var indexer = _indexerFactory.ResolveIndexer(release.IndexerId, release.Indexer);

        if (indexer == null)
        {
            _logger.Debug("Push Release {0} not associated with an indexer.", release.Title);
        }
        else
        {
            _logger.Debug("Push Release {0} associated with indexer '{1} ({2})", release.Title, indexer.Name, indexer.Id);

            release.IndexerId = indexer.Id;
            release.Indexer = indexer.Name;
        }
    }

    private int? ResolveDownloadClientId(ReleasePushResource release)
    {
        var downloadClient = _downloadClientFactory.ResolveDownloadClient(release.DownloadClientId, release.DownloadClientName);

        if (downloadClient == null)
        {
            _logger.Debug("Push Release {0} not associated with a download client.", release.Title);
        }
        else
        {
            _logger.Debug("Push Release {0} associated with download client '{1} ({2})", release.Title, downloadClient.Name, downloadClient.Id);
        }

        return downloadClient?.Id;
    }
}
