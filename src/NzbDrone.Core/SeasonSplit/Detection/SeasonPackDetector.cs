using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.SeasonSplit.Detection
{
    public interface ISeasonPackDetector
    {
        SeasonRange Detect(string title);
        bool IsCompletePack(string title);
        string SyntheticTitle(string original, SeasonRange range, int season);
        string SyntheticGuid(string infohash, int season);
        string SyntheticInfohash(string realHash, int season);
    }

    // Ported from seasonsplitarr/internal/torznab/splitter.go. The plausibility
    // filter (end > start, end-start <= 30) intentionally catches false
    // positives from looser patterns, so we err toward broader matching.
    public sealed class SeasonPackDetector : ISeasonPackDetector
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        private static readonly List<Regex> Patterns = new List<Regex>
        {
            // Consume a whole contiguous run of "Sxx" tokens, capturing the
            // first and last ("S10 S11 S12 S13 S14" -> 10..14, not just 10..11).
            // Getting the full span right keeps the per-season size estimate
            // accurate (total / season-count) so a real ~4.5 GiB season isn't
            // mis-estimated as ~11 GiB and rejected on size. Also matches the
            // plain two-season "S01-S05" / "S01 S19" cases (middle group empty).
            new Regex(@"\bS(\d{1,2})(?:[\s._-]+S\d{1,2})*[-._ ]?S(\d{1,2})\b", Opts),
            new Regex(@"\bS(\d{1,2})\s?[-–]\s?(\d{1,2})\b", Opts),
            new Regex(@"\bS(\d{1,2})\s+(?:to|thru|through)\s+S?(\d{1,2})\b", Opts),
            new Regex(@"\bSeasons?[\s._-]*(\d{1,2})[\s._-]*[-–][\s._-]*(\d{1,2})\b", Opts),
            new Regex(@"\bSeasons?[\s._-]+(\d{1,2})\s+(?:to|thru|through)\s+(\d{1,2})\b", Opts),
            new Regex(@"\bSeries[\s._-]*(\d{1,2})[\s._-]*[-–][\s._-]*(\d{1,2})\b", Opts),
            new Regex(@"\bSeries[\s._-]+(\d{1,2})\s+(?:to|thru|through)\s+(\d{1,2})\b", Opts),
        };

        // Allow the same separators the range patterns do — release titles use
        // dots/underscores ("Show.Complete.Series.1080p"), not just spaces.
        private static readonly Regex CompleteRegex = new Regex(@"\b(Complete[\s._-]+(Series|Collection)|Full[\s._-]+Series)\b", Opts);

        // A single season token ("S10", "Season 10"). Used to scrub leftover
        // season tokens out of a synthetic title — e.g. when a range pattern
        // only captured the first two seasons of a non-contiguous run
        // ("S10 S11 S12 S13 S14"), the tail ("S12 S13 S14") must be removed so
        // the parser sees exactly one season and treats it as a full-season pack.
        private static readonly Regex SeasonTokenRegex =
            new Regex(@"(?<![A-Za-z0-9])(?:S\d{1,2}|Seasons?[\s._-]*\d{1,2})(?![A-Za-z0-9])", Opts);

        public SeasonRange Detect(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return null;
            }

            foreach (var re in Patterns)
            {
                var m = re.Match(title);
                if (!m.Success)
                {
                    continue;
                }

                if (!int.TryParse(m.Groups[1].Value, out var start) ||
                    !int.TryParse(m.Groups[2].Value, out var end))
                {
                    continue;
                }

                if (end <= start || end - start > 30)
                {
                    continue;
                }

                return new SeasonRange
                {
                    Start = start,
                    End = end,
                    MatchedToken = m.Value,
                };
            }

            return null;
        }

        public bool IsCompletePack(string title) =>
            !string.IsNullOrEmpty(title) && CompleteRegex.IsMatch(title);

        public string SyntheticTitle(string original, SeasonRange range, int season)
        {
            var replacement = $"S{season:D2}";
            var idx = original.IndexOf(range.MatchedToken, System.StringComparison.Ordinal);
            if (idx < 0)
            {
                return original;
            }

            // Swap the matched range token for a unique placeholder first, so we
            // can scrub every OTHER season token out of the title (the tail of a
            // non-contiguous run the range pattern didn't fully capture) without
            // also clobbering the season we're keeping.
            const string placeholder = "SEASONSPLIT";
            var stitched = string.Concat(original.AsSpan(0, idx), placeholder, original.AsSpan(idx + range.MatchedToken.Length));

            stitched = SeasonTokenRegex.Replace(stitched, " ");

            // Tidy separators left behind by the scrub so the result still parses
            // cleanly (e.g. "S10..DrM" / "S10  -  DrM" -> "S10 DrM").
            stitched = stitched.Replace(placeholder, replacement);
            stitched = Regex.Replace(stitched, @"[ ._-]{2,}", " ").Trim(' ', '.', '-', '_');

            return stitched;
        }

        public string SyntheticGuid(string infohash, int season) =>
            "seasonsplit-" + SyntheticInfohash(infohash, season);

        // sha256 truncated to 20 bytes / 40 hex chars to match btih length.
        // Not security-sensitive — we just need determinism for per-season ids.
        public string SyntheticInfohash(string realHash, int season)
        {
            var input = (realHash ?? string.Empty).ToLowerInvariant() + ":s" + season;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(40);
            for (var i = 0; i < 20; i++)
            {
                sb.Append(hash[i].ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
