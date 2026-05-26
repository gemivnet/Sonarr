using System;
using System.Collections.Generic;

namespace NzbDrone.Core.SeasonSplit.Download
{
    public sealed class SeasonSplitGrab
    {
        // The synthetic GUID Sonarr saw on the indexer result and is grabbing.
        public string SyntheticGuid { get; init; } = string.Empty;

        // The synthetic infohash we present to the download client so sibling
        // seasons are tracked as distinct queue items even though they share
        // an underlying real torrent.
        public string SyntheticInfoHash { get; init; } = string.Empty;

        // The real infohash of the underlying multi-season pack.
        public string RealInfoHash { get; init; } = string.Empty;

        // Which season of the pack this grab corresponds to. For a consolidated
        // multi-season Add Magnet grab this is the first season; Seasons carries
        // the full set.
        public int Season { get; init; }

        // All seasons this grab covers. Single-season grabs (search/RSS expander)
        // carry one entry; a consolidated Add Magnet grab carries the whole
        // selected set so the include regex is the union of them. Drives
        // BuildSeasonIncludeRegex; never null in practice (the dispatcher always
        // sets it), but readers fall back to [Season] for safety.
        public IReadOnlyList<int> Seasons { get; init; }

        // Explicit per-file include regex. Set for Add Magnet grabs (which can
        // mix whole seasons and individual episodes); when present the download
        // client ships it verbatim instead of rebuilding from Seasons. Null for
        // the search/RSS expander path, which falls back to the season regex.
        public string IncludeRegex { get; init; }

        // The original magnet URL of the pack (real infohash). Required by the
        // download client to actually fetch files.
        public string SourceMagnet { get; init; } = string.Empty;

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
