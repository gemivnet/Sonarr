using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.SeasonSplit;
using NzbDrone.Core.SeasonSplit.Detection;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.SeasonSplitTests
{
    [TestFixture]
    public class SeasonSplitReleaseExpanderFixture : CoreTest
    {
        private SeasonSplitReleaseExpander _expander;

        [SetUp]
        public void Setup()
        {
            _expander = new SeasonSplitReleaseExpander(new SeasonPackDetector(), new Mock<ISeriesService>().Object, LogManager.GetLogger("test"));
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
        public void does_not_split_when_multi_season_packs_are_grabbed_natively()
        {
            // New model: SeasonSplitConfig.AllowMultiSeasonPacks is on, so a pack is
            // grabbed as a single multi-season download rather than fanned out into
            // per-season synthetic siblings. The expander is a no-op (pending its
            // removal) and returns the batch unchanged.
            var releases = new List<ReleaseInfo> { Pack("Show.S01-S05.COMPLETE.1080p.WEB-DL") };

            var result = _expander.Expand(releases, wantedSeasons: new[] { 2 }, seriesTvdbId: 123);

            Synthetics(result).Should().BeEmpty();
            result.Should().BeEquivalentTo(releases);
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
