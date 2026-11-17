namespace AutoOrganizer.Core;

public enum ImportMode { Copy, Move }

public sealed record OrganizerSettings
{
    public string IncomingPath { get; init; } = "";
    public string LibraryPath { get; init; } = "";
    public bool DryRun { get; init; } = true;
    public ImportMode Mode { get; init; } = ImportMode.Copy;
    public int StableSeconds { get; init; } = 300;
    public bool IncludeSeriesYear { get; init; }
    // If set, only top-level download folders containing this file may be imported.
    public string CompletionMarker { get; init; } = "";
}

public sealed record MediaIdentity(string Title, int? Year, int? Season, string? Episodes)
{
    public bool IsSeries => Season.HasValue;

    public string RelativeDestination(string extension, bool includeSeriesYear)
    {
        var title = ReleaseParser.SafeName(Title);
        if (IsSeries)
        {
            var folder = includeSeriesYear && Year.HasValue ? $"{title} ({Year})" : title;
            return Path.Combine("Shows", folder, $"Season {Season:00}", $"{title} - S{Season:00}{Episodes}{extension.ToLowerInvariant()}");
        }

        var movie = $"{title} ({Year})";
        return Path.Combine("Movies", movie, movie + extension.ToLowerInvariant());
    }
}
