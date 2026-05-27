using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.SeasonSplit
{
    // Builds the per-file IncludeRegex rdt-client applies so only the wanted
    // content of a shared pack materialises. Two granularities, combined into one
    // expression so a single torrent can mix whole seasons and individual
    // episodes:
    //   - whole season  -> matches "S03E.." episodes and the "Season 03" folder
    //     form (so episode-less filenames in that season still come through),
    //     while rejecting range folders (S01-S05) and adjacent seasons (S30).
    //   - single episode -> matches "S06E03" and "6x03" forms for that exact
    //     episode only.
    public static class SeasonSplitIncludeRegex
    {
        public static string ForSeasons(IEnumerable<int> seasons)
            => Build(seasons, null);

        // Build a regex matching the given whole seasons and/or individual
        // episodes. Returns null when nothing was requested.
        public static string Build(IEnumerable<int> seasons, IEnumerable<(int Season, int Episode)> episodes)
        {
            var parts = new List<string>();

            var seasonList = (seasons ?? Enumerable.Empty<int>())
                .Where(s => s > 0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (seasonList.Count > 0)
            {
                var alt = string.Join("|", seasonList);
                parts.Add($"S0*(?:{alt})(?=[ ._-]?E\\d)");
                parts.Add($"season[ ._-]*0*(?:{alt})(?![0-9])");

                // "NxNN" numbering (e.g. "12x01") - non-English packs use it (the
                // Anthony Bourdain CZ pack names every file "12x07"). Without this a
                // whole-season grab from such a pack matches nothing ("all files
                // excluded"). The leading (?<![A-Za-z0-9]) on the whole expression
                // keeps it off resolution tokens like "1920x1080".
                parts.Add($"0*(?:{alt})x\\d{{2,3}}(?![0-9])");
            }

            var episodeList = (episodes ?? Enumerable.Empty<(int, int)>())
                .Where(e => e.Season > 0 && e.Episode > 0)
                .Distinct()
                .OrderBy(e => e.Season)
                .ThenBy(e => e.Episode)
                .ToList();

            foreach (var (season, episode) in episodeList)
            {
                parts.Add($"S0*{season}[ ._-]?E0*{episode}(?![0-9])");
                parts.Add($"0*{season}x0*{episode}(?![0-9])");
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return "(?i)(?<![A-Za-z0-9])(?:" + string.Join("|", parts) + ")";
        }
    }
}
