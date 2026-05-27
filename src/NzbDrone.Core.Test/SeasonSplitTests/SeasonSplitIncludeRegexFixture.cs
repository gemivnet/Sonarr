using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.SeasonSplit;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.SeasonSplitTests
{
    [TestFixture]
    public class SeasonSplitIncludeRegexFixture : CoreTest
    {
        private static bool Matches(string pattern, string fileName)
        {
            return pattern != null && Regex.IsMatch(fileName, pattern);
        }

        // Whole-season grabs must match every common episode-numbering form so a
        // pack's files for that season actually come through the rdt-client filter.
        [TestCase("Series.S12E04.1080p.mkv", true)]
        [TestCase("Series S12 E04.mkv", true)]
        [TestCase("Series/Season 12/episode.mkv", true)]
        [TestCase("Anthony Bourdain - Nezn'am'e konciny 12x01 - Kena (HDTV).mp4", true)] // NxNN (CZ pack)
        [TestCase("Anthony Bourdain - Nezname konciny 12x07 - Lower East Side.mp4", true)]
        [TestCase("Series.S11E04.mkv", false)] // adjacent season
        [TestCase("Series 11x05.mp4", false)] // adjacent season, NxNN
        [TestCase("Series.1920x1080.x265.mkv", false)] // resolution must NOT look like 20x10 / 19x?? etc.
        public void whole_season_matches_all_numbering_forms(string fileName, bool expected)
        {
            var pattern = SeasonSplitIncludeRegex.ForSeasons(new[] { 12 });
            Matches(pattern, fileName).Should().Be(expected);
        }

        [TestCase("Series.1920x1080.mkv", false)] // season 20 must not match the resolution
        public void resolution_tokens_do_not_match_adjacent_season(string fileName, bool expected)
        {
            var pattern = SeasonSplitIncludeRegex.ForSeasons(new[] { 20 });
            Matches(pattern, fileName).Should().Be(expected);
        }

        // Single-episode grabs match SxxExx and NxNN for that exact episode only.
        [TestCase("Series.S06E03.mkv", true)]
        [TestCase("Series 6x03.mp4", true)]
        [TestCase("Series.S06E04.mkv", false)]
        [TestCase("Series 6x04.mp4", false)]
        public void single_episode_matches_only_that_episode(string fileName, bool expected)
        {
            var pattern = SeasonSplitIncludeRegex.Build(null, new[] { (6, 3) });
            Matches(pattern, fileName).Should().Be(expected);
        }

        [Test]
        public void returns_null_when_nothing_requested()
        {
            SeasonSplitIncludeRegex.Build(null, null).Should().BeNull();
        }
    }
}
