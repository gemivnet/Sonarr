using System.Linq;
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
        public void synthetic_title_consumes_stranded_season_word()
        {
            // Real-world breakage: "Alone Season S01-S10 ..." -> the range token
            // is "S01-S10" but the leading word "Season" must be consumed too,
            // otherwise we emit "...Season S10..." which the parser can't resolve.
            const string title = "Alone Season S01-S10 Complete (2015-2023) 720p x264 aac 2.0.djd";
            var range = _detector.Detect(title);

            var synthetic = _detector.SyntheticTitle(title, range, 10);

            synthetic.Should().Contain("S10");
            synthetic.Should().NotContain("Season S10");

            // The whole-run year range is dropped (it poisons series-year parsing).
            synthetic.Should().NotContain("2015");
            synthetic.Should().NotContain("2023");
        }

        [Test]
        public void synthetic_title_keeps_single_year_and_balances_parens()
        {
            const string title = "Everybody Loves Raymond S01-S09 (1996) Complete";
            var range = _detector.Detect(title);

            var synthetic = _detector.SyntheticTitle(title, range, 5);

            synthetic.Should().Contain("S05");
            synthetic.Should().Contain("(1996)");
            synthetic.Count(c => c == '(').Should().Be(synthetic.Count(c => c == ')'));
        }

        [TestCase("Alone Season S01-S10 Complete (2015-2023) 720p x264 aac 2.0.djd", 10, "alone")]
        [TestCase("Everybody Loves Raymond S01-S09 (1996) Complete", 5, "everybody loves raymond")]
        [TestCase("Show.S01-S05.1080p.WEB-DL", 3, "show")]
        [TestCase("Whose Line Is It Anyway (US) 1998 Complete S01-S02 TVRip x264 [i c]", 1, "whose line is it anyway")]
        public void synthetic_title_round_trips_to_intended_season(string title, int season, string seriesFragment)
        {
            // A synthetic title is only useful if Sonarr can re-parse it back to
            // the intended series + single season at import time. Guarantee it.
            var range = _detector.Detect(title);

            var synthetic = _detector.SyntheticTitle(title, range, season);
            var parsed = Parser.Parser.ParseTitle(synthetic);

            parsed.Should().NotBeNull(because: synthetic);
            parsed.SeasonNumber.Should().Be(season, because: synthetic);
            parsed.SeriesTitle.ToLowerInvariant().Should().Contain(seriesFragment, because: synthetic);
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
