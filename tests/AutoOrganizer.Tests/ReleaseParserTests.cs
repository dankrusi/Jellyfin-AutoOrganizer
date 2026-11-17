using AutoOrganizer.Core;
using Xunit;

namespace AutoOrganizer.Tests;

public sealed class ReleaseParserTests
{
    [Theory]
    [InlineData("Succession (2018) Season 1-4 S01-S04 (1080p Mixed x265 HEVC 10bit EAC3 5.1 Silence)/Season 1/Succession.S01E01.1080p.WEB-DL.x265.mkv", "Shows/Succession/Season 01/Succession - S01E01.mkv")]
    [InlineData("Succession (2018) Season 1-4 S01-S04 (1080p Mixed x265 HEVC 10bit EAC3 5.1 Silence)/S04/S04E10.mkv", "Shows/Succession/Season 04/Succession - S04E10.mkv")]
    [InlineData("Succession (2018) Season 1-4 S01-S04 (1080p Mixed x265)/Season 01/01 - Celebration.mkv", "Shows/Succession/Season 01/Succession - S01E01.mkv")]
    [InlineData("Succession/Season 02/Episode 03.mkv", "Shows/Succession/Season 02/Succession - S02E03.mkv")]
    [InlineData("Succession Season 1-4/Season 02 (1080p x265)/E03.mkv", "Shows/Succession/Season 02/Succession - S02E03.mkv")]
    [InlineData("The.Last.of.Us.S01E01.2160p.WEB.h265.mkv", "Shows/The Last of Us/Season 01/The Last of Us - S01E01.mkv")]
    [InlineData("Show.Name.1x02.720p.mkv", "Shows/Show Name/Season 01/Show Name - S01E02.mkv")]
    [InlineData("Show.Name.1x02-1x03.720p.mkv", "Shows/Show Name/Season 01/Show Name - S01E02-E03.mkv")]
    [InlineData("1923.S01E01.mkv", "Shows/1923/Season 01/1923 - S01E01.mkv")]
    [InlineData("Show.S01E01-720p.mkv", "Shows/Show/Season 01/Show - S01E01.mkv")]
    [InlineData("Show.S01E01E02.mkv", "Shows/Show/Season 01/Show - S01E01-E02.mkv")]
    [InlineData("Show.S01E01-E03.mkv", "Shows/Show/Season 01/Show - S01E01-E03.mkv")]
    [InlineData("Show.S01E01-02.mkv", "Shows/Show/Season 01/Show - S01E01-E02.mkv")]
    [InlineData("Show.S00E01.mkv", "Shows/Show/Season 00/Show - S00E01.mkv")]
    [InlineData("[Group] Show.Name.S02E10.1080p.mkv", "Shows/Show Name/Season 02/Show Name - S02E10.mkv")]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.x265-GROUP.mkv", "Movies/Dune Part Two (2024)/Dune Part Two (2024).mkv")]
    [InlineData("1917.2019.1080p.mkv", "Movies/1917 (2019)/1917 (2019).mkv")]
    [InlineData("2001.A.Space.Odyssey.1968.1080p.mkv", "Movies/2001 A Space Odyssey (1968)/2001 A Space Odyssey (1968).mkv")]
    [InlineData("Blade.Runner.2049.2017.1080p.mkv", "Movies/Blade Runner 2049 (2017)/Blade Runner 2049 (2017).mkv")]
    [InlineData("www.UIndex.org    -    Sirat (2025) 1080p BluRay 5.1-WORLD/movie.mkv", "Movies/Sirat (2025)/Sirat (2025).mkv")]
    [InlineData("www.UIndex.org - Sirat.2025.1080p.mkv", "Movies/Sirat (2025)/Sirat (2025).mkv")]
    [InlineData("Dune (2021) [1080p] [BluRay] [YTS.MX]/Dune.2021.1080p.BluRay.x264-YTS.MX.mp4", "Movies/Dune (2021)/Dune (2021).mp4")]
    [InlineData("The.Matrix.1999.1080p.BluRay.x265-GROUP/movie.mkv", "Movies/The Matrix (1999)/The Matrix (1999).mkv")]
    public void TorrentNamesProduceJellyfinPaths(string source, string expected)
    {
        var media = ReleaseParser.Parse(source);
        Assert.NotNull(media);
        Assert.Equal(expected, media.RelativeDestination(Path.GetExtension(source), false).Replace('\\', '/'));
    }

    [Theory]
    [InlineData("unknown.mkv")]
    [InlineData("Succession (2018) Season 1-4/unidentified.mkv")]
    [InlineData("Succession/Season 1/Celebration.mkv")]
    [InlineData("Show.S01E01E03.mkv")]
    [InlineData("Show.S01E03-E01.mkv")]
    [InlineData("Show.S01E01-S02E02.mkv")]
    [InlineData("Show.1x01-2x02.mkv")]
    [InlineData("Movie.2024/sample.mkv")]
    [InlineData("Movie.2024/Samples/something.mkv")]
    [InlineData("Movie.2024/Extras/feature.mkv")]
    [InlineData("Movie.2024.CD1.mkv")]
    [InlineData("../Show.S01E01.mkv")]
    public void AmbiguousOrExcludedNamesAreSkipped(string source) => Assert.Null(ReleaseParser.Parse(source));

    [Fact]
    public void SeriesYearIsOptional()
    {
        var media = ReleaseParser.Parse("Succession (2018) Season 1-4/S01/S01E01.mkv");
        Assert.NotNull(media);
        Assert.Contains("Succession (2018)", media.RelativeDestination(".mkv", true));
        Assert.DoesNotContain("2018", media.RelativeDestination(".mkv", false));
    }

    [Fact]
    public void SanitizesReservedCharacters() => Assert.Equal("Law Order", ReleaseParser.SafeName("Law: Order?"));
}
