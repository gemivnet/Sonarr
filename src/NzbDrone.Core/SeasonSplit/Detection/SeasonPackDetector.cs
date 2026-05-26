using System;
using System.Collections.Generic;
using System.Linq;
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

        // Consolidated (multi-season / multi-episode) identity: a deterministic
        // synthetic guid/infohash keyed by an arbitrary string (e.g. the sorted
        // season set), distinct from any single-season id.
        string SyntheticGuid(string infohash, string key);
        string SyntheticInfohash(string realHash, string key);
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

        // A "Season"/"Seasons"/"Series" word (plus trailing separators) sitting
        // immediately before the matched range token. Consumed together with the
        // token, otherwise swapping only the numeric/"Sxx" part strands the word
        // next to the replacement ("Alone Season S01-S10" -> "Alone Season S10",
        // which the parser can't resolve to a series + season at import time).
        private static readonly Regex LeadingSeasonWordRegex =
            new Regex(@"(?:Seasons?|Series)[\s._-]*$", Opts);

        // A multi-year range ("(2015-2023)", "2015 - 2023", "[2015-2023]"). A
        // single per-season release shouldn't carry the whole run's span, and the
        // range makes the parser read a bogus series year, breaking the series
        // match when the download is imported.
        private static readonly Regex YearRangeRegex =
            new Regex(@"[\(\[]?\b(?:19|20)\d{2}[\s._-]*[-–—][\s._-]*(?:19|20)\d{2}\b[\)\]]?", Opts);

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
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(range?.MatchedToken))
            {
                return FallbackTitle(original, range, season);
            }

            var replacement = $"S{season:D2}";
            var idx = original.IndexOf(range.MatchedToken, StringComparison.Ordinal);
            if (idx < 0)
            {
                return FallbackTitle(original, range, season);
            }

            // Extend the match start backward over an immediately-preceding
            // Season/Seasons/Series word so a token like S01-S10 matched without
            // the leading word does not leave it stranded next to the replacement
            // ("...Season S10..."), which the parser cannot resolve on import.
            var start = idx;
            var lead = LeadingSeasonWordRegex.Match(original.Substring(0, idx));
            if (lead.Success && lead.Index + lead.Length == idx)
            {
                start = lead.Index;
            }

            // Swap the matched range token for a unique placeholder first, so we
            // can scrub every OTHER season token out of the title (the tail of a
            // non-contiguous run the range pattern didn't fully capture) without
            // also clobbering the season we're keeping.
            const string placeholder = "SEASONSPLIT";
            var stitched = string.Concat(original.AsSpan(0, start), placeholder, original.AsSpan(idx + range.MatchedToken.Length));

            stitched = SeasonTokenRegex.Replace(stitched, " ");

            // Drop multi-year ranges - wrong for a single season, and they make
            // the parser read a bogus series year and miss the series on import.
            stitched = YearRangeRegex.Replace(stitched, " ");

            // Tidy separators left behind by the scrub so the result still parses
            // cleanly (e.g. "S10..DrM" / "S10  -  DrM" -> "S10 DrM").
            stitched = stitched.Replace(placeholder, replacement);
            stitched = Tidy(stitched);

            // A synthetic title only works if Sonarr can re-parse it back to THIS
            // one season at import time - the download client knows the name, not
            // the TvdbId we stamped at grab time. If the surgery produced anything
            // that does not parse cleanly, fall back to a minimal but guaranteed-
            // parseable form rather than emit an un-importable grab.
            return ParsesToSeason(stitched, season) ? stitched : FallbackTitle(original, range, season);
        }

        // Collapse separator runs left by the scrubs, drop empty/unbalanced
        // brackets, and trim edge punctuation - while preserving single dotted
        // separators ("Show.S03.1080p") that scene releases (and the parser) use.
        private static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            s = Regex.Replace(s, @"\(\s*\)|\[\s*\]", " ");
            s = BalanceBrackets(s);
            return Regex.Replace(s, @"[ ._-]{2,}", " ").Trim(' ', '.', '-', '_');
        }

        // Remove an unmatched bracket so a year-range strip or a truncated source
        // title cannot leave "...(1996" dangling.
        private static string BalanceBrackets(string s)
        {
            if (s.Count(c => c == '(') != s.Count(c => c == ')'))
            {
                s = s.Replace('(', ' ').Replace(')', ' ');
            }

            if (s.Count(c => c == '[') != s.Count(c => c == ']'))
            {
                s = s.Replace('[', ' ').Replace(']', ' ');
            }

            return s;
        }

        // Minimal, guaranteed-parseable title: the series name plus a single
        // season token. Prefer the parser's own view of the series; if even that
        // fails, take the text before the matched range token.
        private string FallbackTitle(string original, SeasonRange range, int season)
        {
            string seriesTitle = null;

            try
            {
                seriesTitle = Parser.Parser.ParseTitle(original ?? string.Empty)?.SeriesTitle;
            }
            catch
            {
                // ignored - fall through to the substring heuristic
            }

            if (string.IsNullOrWhiteSpace(seriesTitle) && !string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(range?.MatchedToken))
            {
                var i = original.IndexOf(range.MatchedToken, StringComparison.Ordinal);
                if (i > 0)
                {
                    seriesTitle = original.Substring(0, i);
                }
            }

            seriesTitle = Tidy(seriesTitle ?? string.Empty);
            if (string.IsNullOrWhiteSpace(seriesTitle))
            {
                seriesTitle = "Unknown Series";
            }

            return $"{seriesTitle} S{season:D2}";
        }

        // True when Sonarr's own parser reads the title back as the intended
        // single season with a non-empty series title.
        private static bool ParsesToSeason(string title, int season)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            try
            {
                var parsed = Parser.Parser.ParseTitle(title);
                return parsed != null &&
                       !string.IsNullOrWhiteSpace(parsed.SeriesTitle) &&
                       parsed.SeasonNumber == season;
            }
            catch
            {
                return false;
            }
        }

        public string SyntheticGuid(string infohash, int season) =>
            "seasonsplit-" + SyntheticInfohash(infohash, season);

        public string SyntheticGuid(string infohash, string key) =>
            "seasonsplit-" + SyntheticInfohash(infohash, key);

        // sha256 truncated to 20 bytes / 40 hex chars to match btih length.
        // Not security-sensitive — we just need determinism for per-season ids.
        public string SyntheticInfohash(string realHash, int season) =>
            SyntheticInfohash(realHash, "s" + season);

        public string SyntheticInfohash(string realHash, string key)
        {
            var input = (realHash ?? string.Empty).ToLowerInvariant() + ":" + (key ?? string.Empty);
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
