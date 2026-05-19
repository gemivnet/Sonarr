using System;

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

        // Which season of the pack this grab corresponds to.
        public int Season { get; init; }

        // The original magnet URL of the pack (real infohash). Required by the
        // download client to actually fetch files.
        public string SourceMagnet { get; init; } = string.Empty;

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
