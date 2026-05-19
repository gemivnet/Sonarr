namespace NzbDrone.Core.SeasonSplit.Detection
{
    public sealed class SeasonRange
    {
        public int Start { get; init; }
        public int End { get; init; }

        // The literal substring in the original title that should be replaced
        // when synthesising a per-season title (e.g. "S01-S05", "Seasons 1-5").
        public string MatchedToken { get; init; } = string.Empty;

        public int Count => End - Start + 1;
    }
}
