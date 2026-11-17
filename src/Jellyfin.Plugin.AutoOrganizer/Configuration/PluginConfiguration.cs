using AutoOrganizer.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoOrganizer.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public string IncomingPath { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public string ShowsPath { get; set; } = "";
    public string MoviesPath { get; set; } = "";
    public ImportMode Mode { get; set; } = ImportMode.Copy;
    public int StableSeconds { get; set; } = 300;
    public bool IncludeSeriesYear { get; set; }
    public string CompletionMarker { get; set; } = "";

    public OrganizerSettings ToSettings() => new()
    {
        IncomingPath = IncomingPath,
        LibraryPath = LibraryPath,
        ShowsPath = ShowsPath,
        MoviesPath = MoviesPath,
        DryRun = DryRun,
        Mode = Mode,
        StableSeconds = StableSeconds,
        IncludeSeriesYear = IncludeSeriesYear,
        CompletionMarker = CompletionMarker
    };
}
