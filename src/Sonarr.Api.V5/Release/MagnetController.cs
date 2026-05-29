using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.SeasonSplit.Preview;
using Sonarr.Http;

namespace Sonarr.Api.V5.Release;

// "Add Magnet" backend: probe a pasted magnet for its real debrid-provider file list, then
// build a per-season preview (real sizes + file-parsed quality + decision-engine
// verdicts) the UI renders interactive-search-style with checkboxes.
[V5ApiController]
public class MagnetController : Controller
{
    private readonly IMagnetProbeService _magnetProbeService;
    private readonly IMagnetPreviewService _magnetPreviewService;

    public MagnetController(IMagnetProbeService magnetProbeService,
                           IMagnetPreviewService magnetPreviewService)
    {
        _magnetProbeService = magnetProbeService;
        _magnetPreviewService = magnetPreviewService;
    }

    // Raw file list straight from the debrid provider (diagnostic / building block).
    [HttpPost("probe")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public Ok<MagnetProbeResult> Probe([FromBody] MagnetProbeRequest request)
    {
        return TypedResults.Ok(_magnetProbeService.Probe(request.MagnetUrl));
    }

    // Per-season preview: season, real size, file count, file-parsed quality, and
    // the decision-engine verdict (Approved drives default-checked; rejections are
    // the same warnings interactive search shows).
    [HttpPost("preview")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public Ok<List<MagnetSeasonPreviewResource>> Preview([FromBody] MagnetPreviewRequest request)
    {
        var previews = _magnetPreviewService.Preview(request.MagnetUrl, request.TvdbId, request.IncludeSatisfied);

        return TypedResults.Ok(previews.Select(MapPreview).ToList());
    }

    // Grab the ticked seasons. Overrides soft rejections (quality not wanted, not
    // an upgrade, already have) but still refuses anything over its size limit.
    [HttpPost("grab")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public Ok<MagnetGrabResultResource> Grab([FromBody] MagnetGrabRequest request)
    {
        var episodes = (request.Episodes ?? new List<MagnetGrabEpisodeRequest>())
            .Select(e => new MagnetEpisodeSelection { Season = e.Season, Episode = e.Episode })
            .ToList();

        var r = _magnetPreviewService.Grab(request.MagnetUrl, request.TvdbId, request.Seasons ?? new List<int>(), episodes, request.DownloadClientId);

        return TypedResults.Ok(new MagnetGrabResultResource
        {
            Grabbed = r.Grabbed,
            Skipped = r.Skipped.Select(s => new MagnetGrabSkipResource { Season = s.Season, Reason = s.Reason }).ToList(),
        });
    }

    private static MagnetSeasonPreviewResource MapPreview(MagnetSeasonPreview p)
    {
        var decision = p.Decision;
        var parsed = decision?.RemoteEpisode?.ParsedEpisodeInfo;

        var rejections = decision?.Rejections?.Select(r => r.Message).ToList() ?? new List<string>();

        // A season Sonarr has no episodes for can't be mapped, so the engine
        // rejects it with a cryptic "unable to identify episodes". Say what's
        // actually wrong: it's a metadata gap, not a grab failure.
        if (p.EpisodeCount == 0)
        {
            rejections = new List<string>
            {
                "Sonarr has no episodes for this season — refresh the series (it may not exist on TheTVDB).",
            };
        }

        return new MagnetSeasonPreviewResource
        {
            Season = p.Season,
            Title = p.Title,
            Size = p.Size,
            FileCount = p.FileCount,
            EpisodeCount = p.EpisodeCount,
            ExistingCount = p.ExistingCount,
            Satisfied = p.Satisfied,
            Quality = parsed?.Quality?.Quality?.Name,
            Approved = decision?.Approved ?? false,
            Rejections = rejections,
            Guid = decision?.RemoteEpisode?.Release?.Guid,
            Episodes = p.Episodes.Select(e => new MagnetEpisodePreviewResource
            {
                Episode = e.Episode,
                Title = e.Title,
                Size = e.Size,
                Quality = e.Quality,
                HasFile = e.HasFile,
            }).ToList(),
        };
    }
}

public class MagnetProbeRequest
{
    public string? MagnetUrl { get; set; }
}

public class MagnetPreviewRequest
{
    public string? MagnetUrl { get; set; }
    public int TvdbId { get; set; }
    public bool IncludeSatisfied { get; set; }
}

public class MagnetSeasonPreviewResource
{
    public int Season { get; set; }
    public string? Title { get; set; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public int EpisodeCount { get; set; }
    public int ExistingCount { get; set; }
    public bool Satisfied { get; set; }
    public string? Quality { get; set; }
    public bool Approved { get; set; }
    public List<string> Rejections { get; set; } = new List<string>();
    public string? Guid { get; set; }
    public List<MagnetEpisodePreviewResource> Episodes { get; set; } = new List<MagnetEpisodePreviewResource>();
}

public class MagnetEpisodePreviewResource
{
    public int Episode { get; set; }
    public string? Title { get; set; }
    public long Size { get; set; }
    public string? Quality { get; set; }
    public bool HasFile { get; set; }
}

public class MagnetGrabRequest
{
    public string? MagnetUrl { get; set; }
    public int TvdbId { get; set; }
    public List<int>? Seasons { get; set; }
    public List<MagnetGrabEpisodeRequest>? Episodes { get; set; }
    public int? DownloadClientId { get; set; }
}

public class MagnetGrabEpisodeRequest
{
    public int Season { get; set; }
    public int Episode { get; set; }
}

public class MagnetGrabSkipResource
{
    public int Season { get; set; }
    public string? Reason { get; set; }
}

public class MagnetGrabResultResource
{
    public List<int> Grabbed { get; set; } = new List<int>();
    public List<MagnetGrabSkipResource> Skipped { get; set; } = new List<MagnetGrabSkipResource>();
}
