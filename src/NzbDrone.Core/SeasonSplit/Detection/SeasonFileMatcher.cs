using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.SeasonSplit.Detection
{
    public interface ISeasonFileMatcher
    {
        // Returns the season number embedded in a file path, or 0 if none
        // can be confidently inferred.
        int FromFilename(string path);
    }

    // Ported from seasonsplitarr/internal/seasonparse.
    public sealed class SeasonFileMatcher : ISeasonFileMatcher
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        private static readonly List<Regex> Patterns = new List<Regex>
        {
            new Regex(@"\bS(\d{1,2})[\s._-]?E\d{1,3}\b", Opts),
            new Regex(@"\b(\d{1,2})x\d{1,3}\b", Opts),
            new Regex(@"\bSeason[\s._-]*(\d{1,2})\b", Opts),
            new Regex(@"\bSeries[\s._-]*(\d{1,2})\b", Opts),
            new Regex(@"\bS(\d{1,2})\b", Opts),
        };

        public int FromFilename(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            var basename = Path.GetFileName(path);
            var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
            var candidates = new[] { basename, parent, path };

            foreach (var re in Patterns)
            {
                foreach (var c in candidates)
                {
                    if (string.IsNullOrEmpty(c))
                    {
                        continue;
                    }

                    var m = re.Match(c);
                    if (!m.Success)
                    {
                        continue;
                    }

                    if (int.TryParse(m.Groups[1].Value.TrimStart('0').Length == 0 ? "0" : m.Groups[1].Value.TrimStart('0'), out var n)
                        && n > 0 && n < 100)
                    {
                        return n;
                    }
                }
            }

            return 0;
        }
    }
}
