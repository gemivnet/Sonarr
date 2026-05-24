using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.SeasonSplit.Preview;
using Sonarr.Http;

namespace Sonarr.Api.V5.Release;

// Phase 1a of the "Add Magnet" feature: probe a pasted magnet for its real file
// list + sizes (read back from Real-Debrid via the download client). This is the
// data a per-season preview is built on; the preview/grab endpoints layer on top.
[V5ApiController]
public class MagnetController : Controller
{
    private readonly IMagnetProbeService _magnetProbeService;

    public MagnetController(IMagnetProbeService magnetProbeService)
    {
        _magnetProbeService = magnetProbeService;
    }

    [HttpPost("probe")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public Ok<MagnetProbeResult> Probe([FromBody] MagnetProbeRequest request)
    {
        return TypedResults.Ok(_magnetProbeService.Probe(request.MagnetUrl));
    }
}

public class MagnetProbeRequest
{
    public string? MagnetUrl { get; set; }
}
