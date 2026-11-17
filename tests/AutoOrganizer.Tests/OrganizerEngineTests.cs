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
    public async Task MoveWorksAcrossFoldersAndRetainsUnrelatedFiles()
    {
        var source = Add("The.Matrix.1999.1080p/The.Matrix.1999.1080p.mkv");
        var subtitle = Add("The.Matrix.1999.1080p/The.Matrix.1999.1080p.srt");
        var unrelated = Add("The.Matrix.1999.1080p/release.nfo");
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Imported);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(subtitle));
        Assert.True(File.Exists(unrelated));
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
        var report = await SettledScan(new(State), Settings with { Mode = ImportMode.Move });
        Assert.Equal(1, report.Errors);
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
