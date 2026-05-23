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
            new Regex(@"\bS(\d{1,2})[-._ ]?S(\d{1,2})\b", Opts),
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

            return string.Concat(original.AsSpan(0, idx), replacement, original.AsSpan(idx + range.MatchedToken.Length));
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
