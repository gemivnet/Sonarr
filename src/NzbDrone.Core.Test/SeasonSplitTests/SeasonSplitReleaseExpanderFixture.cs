using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeasonSplit;
using NzbDrone.Core.SeasonSplit.Detection;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.SeasonSplitTests
{
    [TestFixture]
    public class SeasonSplitReleaseExpanderFixture : CoreTest
    {
        private SeasonSplitReleaseExpander _expander;

        [SetUp]
        public void Setup()
        {
            _expander = new SeasonSplitReleaseExpander(new SeasonPackDetector(), LogManager.GetLogger("test"));
        }

        private static TorrentInfo Pack(string title)
        {
            return new TorrentInfo
            {
                Title = title,
                Guid = "guid-" + title,
                InfoHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                DownloadUrl = "magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                Size = 9_000_000_000,
            };
        }

        private static List<ReleaseInfo> Synthetics(IEnumerable<ReleaseInfo> result)
        {
            return result.Where(r => r.Guid != null && r.Guid.StartsWith("seasonsplit-")).ToList();
        }

        [Test]
        public void emits_a_synthetic_for_every_season_in_the_pack_regardless_of_wanted_season()
        {
            var releases = new List<ReleaseInfo> { Pack("Anthony Bourdain No Reservations S01-03") };

            // Searching only season 2 (the pack happens to surface under that query)
            // must still produce the S01 and S03 splits, so the user can grab any
            // season the pack covers from the one search it appears in.
            var result = _expander.Expand(releases, wantedSeasons: new[] { 2 }, seriesTvdbId: 123);

            var synthetics = Synthetics(result);
            synthetics.Should().HaveCount(3);

            var titles = string.Join(" | ", synthetics.Select(s => s.Title));
            titles.Should().Contain("S01");
            titles.Should().Contain("S02");
            titles.Should().Contain("S03");
        }

        [Test]
        public void emits_all_seasons_even_with_no_wanted_filter()
        {
            var releases = new List<ReleaseInfo> { Pack("Show.S01-S05.COMPLETE.1080p.WEB-DL") };

            var result = _expander.Expand(releases);

            Synthetics(result).Should().HaveCount(5);
        }

        [Test]
        public void leaves_non_pack_releases_untouched()
        {
            var releases = new List<ReleaseInfo> { Pack("Show.S03.1080p.WEB-DL") };

            var result = _expander.Expand(releases, wantedSeasons: new[] { 3 });

            Synthetics(result).Should().BeEmpty();
            result.Should().HaveCount(1);
        }
    }
}
