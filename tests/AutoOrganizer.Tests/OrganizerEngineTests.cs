using AutoOrganizer.Core;
using Xunit;

namespace AutoOrganizer.Tests;

public sealed class OrganizerEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AutoOrganizerTests-" + Guid.NewGuid().ToString("N"));
    private string Incoming => Path.Combine(_root, "incoming");
    private string Library => Path.Combine(_root, "library");
    private string State => Path.Combine(_root, "state");
    private OrganizerSettings Settings => new() { IncomingPath = Incoming, LibraryPath = Library, StableSeconds = 1, DryRun = false };

    public OrganizerEngineTests() => Directory.CreateDirectory(Incoming);

    private string Add(string name, string content = "test-media-content")
    {
        var path = Path.Combine(Incoming, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    private static async Task<ScanReport> SettledScan(OrganizerEngine engine, OrganizerSettings settings)
    {
        await engine.ScanAsync(settings);
        await Task.Delay(1100);
        return await engine.ScanAsync(settings);
    }

    [Fact]
    public async Task DryRunDoesNotCreateLibraryOrReceipts()
    {
        var source = Add("Succession.S01E01.mkv");
        var report = await SettledScan(new(State), Settings with { DryRun = true });
        Assert.Equal(1, report.Previewed);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(Library));
        Assert.False(File.Exists(Path.Combine(State, "receipts.json")));
    }

    [Fact]
    public async Task CopyImportsSeriesAndSidecarsAndSurvivesRestart()
    {
        var source = Add("Succession (2018) Season 1-4/Season 01/Succession.S01E01.1080p.mkv");
        Add("Succession (2018) Season 1-4/Season 01/Succession.S01E01.1080p.en.forced.srt", "subtitles");
        var report = await SettledScan(new(State), Settings);
        Assert.Equal(1, report.Imported);
        Assert.True(File.Exists(source));
        Assert.Equal("test-media-content", File.ReadAllText(Path.Combine(Library, "Shows", "Succession", "Season 01", "Succession - S01E01.mkv")));
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(Library, "Shows", "Succession", "Season 01", "Succession - S01E01.en.forced.srt")));
        var repeat = await SettledScan(new(State), Settings);
        Assert.Equal(0, repeat.Imported);
        Assert.Equal(0, repeat.Errors);
    }

    [Fact]
    public async Task MovePreservesReleaseFilesAndRemovesEmptySourceFolder()
    {
        var source = Add("The.Matrix.1999.1080p/The.Matrix.1999.1080p.mkv");
        var subtitle = Add("The.Matrix.1999.1080p/The.Matrix.1999.1080p.srt");
        var unrelated = Add("The.Matrix.1999.1080p/release.nfo");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Imported);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(subtitle));
        Assert.False(File.Exists(unrelated));
        Assert.False(Directory.Exists(Path.GetDirectoryName(unrelated)!));
        Assert.True(File.Exists(Path.Combine(Library, "Movies", "The Matrix (1999)", ".download-extras", "The.Matrix.1999.1080p", "release.nfo")));
        Assert.True(File.Exists(Path.Combine(Library, "Movies", "The Matrix (1999)", ".download-extras", ".ignore")));
        Assert.True(File.Exists(Path.Combine(Library, "Movies", "The Matrix (1999)", "The Matrix (1999).mkv")));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwrittenAndMoveKeepsSource()
    {
        var source = Add("Dune.2021.mkv");
        var destination = Path.Combine(Library, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "existing-important-movie");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Errors);
        Assert.Equal("existing-important-movie", File.ReadAllText(destination));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task SidecarConflictPreventsVideoImport()
    {
        var source = Add("Dune.2021.mkv");
        var subtitle = Add("Dune.2021.en.srt");
        var destination = Path.Combine(Library, "Movies", "Dune (2021)", "Dune (2021).en.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "existing-subtitle");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Errors);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(subtitle));
        Assert.False(File.Exists(Path.ChangeExtension(destination, null) + ".mkv"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, "Dune (2021).mkv")));
        Assert.Equal("existing-subtitle", File.ReadAllText(destination));
    }

    [Fact]
    public async Task UnrelatedSubtitleIsNotMoved()
    {
        Add("Dune.2021.mkv");
        var unrelated = Add("Dune.2021.Other.en.srt");
        // A suffix under the video stem intentionally belongs to this movie.
        var differentTitle = Add("Other.Movie.2020.en.srt");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Imported);
        Assert.True(File.Exists(differentTitle));
        Assert.False(File.Exists(unrelated));
    }

    [UnixFact]
    public async Task SymlinkedDestinationCannotEscapeLibrary()
    {
        var source = Add("Dune.2021.mkv");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Library);
        var link = Path.Combine(Library, "Movies");
        Directory.CreateSymbolicLink(link, outside);
        await Assert.ThrowsAsync<IOException>(() => new OrganizerEngine(State).ScanAsync(Settings with { Mode = ImportMode.Move }));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
    }

    [UnixFact]
    public async Task SymlinkedSourcesAreNotImported()
    {
        var original = Path.Combine(_root, "outside.mkv");
        File.WriteAllText(original, "outside-content");
        var link = Path.Combine(Incoming, "Dune.2021.mkv");
        File.CreateSymbolicLink(link, original);
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Empty(report.Entries);
        Assert.True(File.Exists(original));
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public async Task FirstObservationAlwaysWaitsEvenForOldFile()
    {
        Add("Dune.2021.mkv");
        var report = await new OrganizerEngine(State).ScanAsync(Settings);
        Assert.Equal("Waiting", Assert.Single(report.Entries).Status);
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public async Task SourceModificationResetsStabilityWindow()
    {
        var file = Add("Dune.2021.mkv");
        var engine = new OrganizerEngine(State);
        await engine.ScanAsync(Settings);
        await Task.Delay(1100);
        File.AppendAllText(file, "more data");
        var report = await engine.ScanAsync(Settings);
        Assert.Equal("Waiting", Assert.Single(report.Entries).Status);
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public async Task PartialFilesBlockWholeDownloadPack()
    {
        Add("Show.S01/Show.S01E01.mkv");
        Add("Show.S01/Show.S01E02.mkv.!qB");
        var report = await SettledScan(new(State), Settings);
        Assert.Equal("Waiting", Assert.Single(report.Entries).Status);
        Assert.False(Directory.Exists(Library));
    }

    [Fact]
    public async Task MarkerGatesImportAndLooseFilesAreSkipped()
    {
        Add("pack/Dune.2021.mkv");
        Add("Other.2020.mkv");
        var engine = new OrganizerEngine(State);
        var settings = Settings with { CompletionMarker = ".completed" };
        var first = await SettledScan(engine, settings);
        Assert.Equal(0, first.Imported);
        Add("pack/.completed", "done");
        var second = await engine.ScanAsync(settings);
        Assert.Equal(1, second.Imported);
        Assert.True(File.Exists(Path.Combine(Incoming, "Other.2020.mkv")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOverlappingRoots(bool libraryInside)
    {
        var settings = libraryInside ? Settings with { LibraryPath = Path.Combine(Incoming, "library") } : Settings with { IncomingPath = Path.Combine(Library, "incoming") };
        await Assert.ThrowsAsync<ArgumentException>(() => new OrganizerEngine(State).ScanAsync(settings));
    }

    [Fact]
    public async Task LateSubtitlesAreImportedWithoutRecopyingVideo()
    {
        Add("Dune.2021.mkv");
        var engine = new OrganizerEngine(State);
        await SettledScan(engine, Settings);
        Add("Dune.2021.en.srt", "late subtitles");
        var report = await SettledScan(engine, Settings);
        Assert.Equal(1, report.Imported);
        Assert.Equal("late subtitles", File.ReadAllText(Path.Combine(Library, "Movies", "Dune (2021)", "Dune (2021).en.srt")));
    }

    [Fact]
    public async Task ChangedSourceDoesNotReuseCopyReceipt()
    {
        var source = Add("Dune.2021.mkv");
        var engine = new OrganizerEngine(State);
        await SettledScan(engine, Settings);
        File.AppendAllText(source, "changed");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        var report = await SettledScan(engine, Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Errors);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ExistingLibraryDestinationsAreUsedWithoutExtraCategoryFolders()
    {
        Add("Caminandes.S01E01.mkv");
        Add("Big.Buck.Bunny.2008.mkv");
        var shows = Path.Combine(_root, "existing-tv-library");
        var movies = Path.Combine(_root, "existing-film-library");
        var settings = Settings with { LibraryPath = "", ShowsPath = shows, MoviesPath = movies };
        var report = await SettledScan(new(State), settings);
        Assert.Equal(2, report.Imported);
        Assert.Equal(0, report.Errors);
        Assert.True(File.Exists(Path.Combine(shows, "Caminandes", "Season 01", "Caminandes - S01E01.mkv")));
        Assert.True(File.Exists(Path.Combine(movies, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv")));
        Assert.False(Directory.Exists(Path.Combine(shows, "Shows")));
        Assert.False(Directory.Exists(Path.Combine(movies, "Movies")));
    }

    [Fact]
    public async Task OneOverrideKeepsSharedRootFallbackForOtherMedia()
    {
        Add("Caminandes.S01E01.mkv");
        Add("Big.Buck.Bunny.2008.mkv");
        var movies = Path.Combine(_root, "existing-film-library");
        var report = await SettledScan(new(State), Settings with { MoviesPath = movies });
        Assert.Equal(2, report.Imported);
        Assert.True(File.Exists(Path.Combine(Library, "Shows", "Caminandes", "Season 01", "Caminandes - S01E01.mkv")));
        Assert.True(File.Exists(Path.Combine(movies, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverridesCannotOverlapIncoming(bool shows)
    {
        var settings = shows ? Settings with { ShowsPath = Path.Combine(Incoming, "tv") } : Settings with { MoviesPath = Incoming };
        await Assert.ThrowsAsync<ArgumentException>(() => new OrganizerEngine(State).ScanAsync(settings));
    }

    [Fact]
    public async Task MissingRootRequiresBothOverrides()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OrganizerEngine(State).ScanAsync(Settings with { LibraryPath = "", ShowsPath = Path.Combine(_root, "tv") }));
    }

    [Fact]
    public async Task OverlappingMediaDestinationsAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new OrganizerEngine(State).ScanAsync(Settings with { ShowsPath = Library, MoviesPath = Path.Combine(Library, "nested-movies") }));
    }

    [Fact]
    public async Task TorrentReleaseFolderPreservesNestedHiddenZeroByteAndSampleFiles()
    {
        const string release = "www.UIndex.org    -    Sirat (2025) 1080p BluRay 5.1-WORLD";
        Add(release + "/movie.mkv");
        Add(release + "/movie.en.srt", "subtitles");
        Add(release + "/readme.txt", "torrent notes");
        Add(release + "/art/poster.jpg", "poster");
        Add(release + "/.completed", "");
        Add(release + "/Sample/sample.mkv", "sample video");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move, CompletionMarker = ".completed" });
        Assert.Equal(0, report.Errors);
        var title = Path.Combine(Library, "Movies", "Sirat (2025)");
        Assert.True(File.Exists(Path.Combine(title, "Sirat (2025).mkv")));
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(title, "Sirat (2025).en.srt")));
        var archive = Path.Combine(title, ".download-extras", ReleaseParser.SafeName(release));
        Assert.Equal("torrent notes", File.ReadAllText(Path.Combine(archive, "readme.txt")));
        Assert.Equal("poster", File.ReadAllText(Path.Combine(archive, "art", "poster.jpg")));
        Assert.Equal("sample video", File.ReadAllText(Path.Combine(archive, "Sample", "sample.mkv")));
        Assert.True(File.Exists(Path.Combine(archive, ".completed")));
        Assert.False(Directory.Exists(Path.Combine(Incoming, release)));
        Assert.Contains(report.Entries, e => e.Status == "Cleaned");
        Assert.True(Directory.Exists(Incoming));
    }

    [Fact]
    public async Task CopyLeavesEntireOriginalReleaseUntouched()
    {
        var movie = Add("Big.Buck.Bunny.2008/movie.mkv");
        var junk = Add("Big.Buck.Bunny.2008/readme.txt");
        var report = await SettledScan(new(State), Settings);
        Assert.Equal(0, report.Errors);
        Assert.True(File.Exists(movie));
        Assert.True(File.Exists(junk));
        Assert.False(Directory.Exists(Path.Combine(Library, "Movies", "Big Buck Bunny (2008)", ".download-extras")));
    }

    [Fact]
    public async Task SeriesPackPreservesAttachmentsOnceUnderSeriesAndCleansAllSeasons()
    {
        Add("Caminandes.S01-S02/Season 01/Caminandes.S01E01.mkv");
        Add("Caminandes.S01-S02/Season 02/Caminandes.S02E01.mkv");
        Add("Caminandes.S01-S02/release.nfo", "notes");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(2, report.Imported);
        Assert.Equal(0, report.Errors);
        Assert.True(File.Exists(Path.Combine(Library, "Shows", "Caminandes", ".download-extras", "Caminandes.S01-S02", "release.nfo")));
        Assert.False(Directory.Exists(Path.Combine(Incoming, "Caminandes.S01-S02")));
    }

    [Fact]
    public async Task UnknownMainVideoPreventsReleaseCleanup()
    {
        Add("release/Big.Buck.Bunny.2008.mkv");
        var unknown = Add("release/unknown.mkv");
        var junk = Add("release/readme.txt");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(0, report.Errors);
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(junk));
        Assert.Contains(report.Entries, e => e.Detail.Contains("unimported video"));
    }

    [Fact]
    public async Task FailedAttachmentTransferKeepsSourcesAndCanResumeAfterRestart()
    {
        Add("Big.Buck.Bunny.2008/movie.mkv");
        var junk = Add("Big.Buck.Bunny.2008/readme.txt", "source notes");
        var archive = Path.Combine(Library, "Movies", "Big Buck Bunny (2008)", ".download-extras", "Big.Buck.Bunny.2008", "readme.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllText(archive, "existing notes");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Errors);
        Assert.True(File.Exists(junk));
        Assert.Equal("existing notes", File.ReadAllText(archive));
        // Resolve the conflict by retaining the pre-existing attachment elsewhere.
        File.Move(archive, archive + ".backup");
        var retry = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(0, retry.Errors);
        Assert.Equal("source notes", File.ReadAllText(archive));
        Assert.False(Directory.Exists(Path.Combine(Incoming, "Big.Buck.Bunny.2008")));
    }

    [Fact]
    public async Task DamagedLedgerFailsWithoutDeletingSources()
    {
        var source = Add("Dune.2021.mkv");
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "receipts.json"), "broken-json");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new OrganizerEngine(State).ScanAsync(Settings));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task CancellationDoesNotImport()
    {
        var source = Add("Dune.2021.mkv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrganizerEngine(State).ScanAsync(Settings, cts.Token));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ChangingAttachmentWaitsThenFinishesUsingReceiptAfterVideoHasMoved()
    {
        Add("Big.Buck.Bunny.2008/movie.mkv");
        var junk = Add("Big.Buck.Bunny.2008/readme.txt", "notes");
        var engine = new OrganizerEngine(State);
        var settings = Settings with { Mode = ImportMode.Move };
        await engine.ScanAsync(settings);
        await Task.Delay(1100);
        File.AppendAllText(junk, " still downloading");
        var first = await engine.ScanAsync(settings);
        Assert.Equal(1, first.Imported);
        Assert.True(File.Exists(junk));
        Assert.Contains(first.Entries, e => e.Detail.Contains("remaining release files to stop changing"));
        var restarted = await SettledScan(new(State), settings);
        Assert.Equal(0, restarted.Errors);
        Assert.False(Directory.Exists(Path.Combine(Incoming, "Big.Buck.Bunny.2008")));
        Assert.Equal("notes still downloading", File.ReadAllText(Path.Combine(Library, "Movies", "Big Buck Bunny (2008)", ".download-extras", "Big.Buck.Bunny.2008", "readme.txt")));
    }

    [UnixFact]
    public async Task ReleaseCleanupDoesNotFollowOrDeleteSymbolicLinks()
    {
        Add("Big.Buck.Bunny.2008/movie.mkv");
        var outside = Path.Combine(_root, "outside-attachments");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "important.txt"), "keep me");
        var source = Path.Combine(Incoming, "Big.Buck.Bunny.2008");
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(0, report.Errors);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "important.txt")));
        Assert.True(Directory.Exists(source));
        Assert.Contains(report.Entries, e => e.Status == "Waiting" && e.Detail.Contains("links"));
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("AutoOrganizerTests-", StringComparison.Ordinal))
            Directory.Delete(resolved, recursive: true);
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Symlink creation requires Windows privileges; this check runs in Linux CI.";
    }
}
