using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.SeasonSplit.Detection;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.SeasonSplitTests
{
    [TestFixture]
    public class SeasonPackDetectorFixture : CoreTest
    {
        private SeasonPackDetector _detector;

        [SetUp]
        public void Setup()
        {
            _detector = new SeasonPackDetector();
        }

        [TestCase("Show.S01-S05.COMPLETE.1080p.WEB-DL", 1, 5)]
        [TestCase("Show.S01.S05.1080p", 1, 5)]
        [TestCase("Show S01-05 1080p", 1, 5)]
        [TestCase("Anthony Bourdain No Reservations S01-03", 1, 3)]

        // Contiguous "Sxx" runs must capture the full span (first..last), not
        // just the first two — otherwise the per-season size estimate is wrong.
        [TestCase("Mayday.Part 2/2.S10 S11 S12 S13 S14.DrM", 10, 14)]
        [TestCase("Show S01 S02 S03 1080p", 1, 3)]
        [TestCase("Air Crash Investigation (Mayday) S01 S19 Complete", 1, 19)]
        [TestCase("Show S01 to S05 1080p", 1, 5)]
        [TestCase("Show S01 through S05 1080p", 1, 5)]
        [TestCase("Show Seasons 1-5 1080p", 1, 5)]
        [TestCase("Show Season 1-5 1080p", 1, 5)]
        [TestCase("Show Seasons 1 to 5 1080p", 1, 5)]
        [TestCase("Show Series 1-5 1080p", 1, 5)]
        [TestCase("Show Series 1 to 5 1080p", 1, 5)]
        public void should_detect_multi_season_range(string title, int start, int end)
        {
            var range = _detector.Detect(title);
            range.Should().NotBeNull();
            range.Start.Should().Be(start);
            range.End.Should().Be(end);
        }

        [TestCase("Show.S01E05.1080p")]
        [TestCase("Show.S03.1080p")]
        [TestCase("Show.2020.1080p")]
        [TestCase("Show.S05-S01.1080p")] // reversed: end <= start
        [TestCase("random release")]
        public void should_not_detect(string title)
        {
            _detector.Detect(title).Should().BeNull();
        }

        [TestCase("Show.Complete.Series.1080p", true)]
        [TestCase("Show.Complete.Collection.1080p", true)]
        [TestCase("Show.Full.Series.1080p", true)]
        [TestCase("Show.S01-S05.1080p", false)]
        public void should_detect_complete_pack(string title, bool expected)
        {
            _detector.IsCompletePack(title).Should().Be(expected);
        }

        [Test]
        public void synthetic_title_replaces_matched_token()
        {
            var range = _detector.Detect("Show.S01-S05.1080p.WEB-DL");
            _detector.SyntheticTitle("Show.S01-S05.1080p.WEB-DL", range, 3)
                .Should().Be("Show.S03.1080p.WEB-DL");
        }

        [Test]
        public void synthetic_title_scrubs_leftover_season_tokens()
        {
            const string title = "Mayday.Part 2/2.S10 S11 S12 S13 S14.DrM";
            var range = _detector.Detect(title);

            // Whole run captured (10..14), so the synthetic title carries exactly
            // one season token and no leftover "S12 S13 S14" residue that would
            // garble parsing.
            var synthetic = _detector.SyntheticTitle(title, range, 10);
            synthetic.Should().Contain("S10");
            synthetic.Should().NotContain("S11");
            synthetic.Should().NotContain("S12");
            synthetic.Should().NotContain("S14");
        }

        [Test]
        public void synthetic_infohash_is_deterministic_and_40_hex()
        {
            var hash = "0123456789abcdef0123456789abcdef01234567";
            var a = _detector.SyntheticInfohash(hash, 3);
            var b = _detector.SyntheticInfohash(hash, 3);
            var c = _detector.SyntheticInfohash(hash, 4);
            a.Should().Be(b);
            a.Should().NotBe(c);
            a.Length.Should().Be(40);
        }
    }
}
