using AutoOrganizer.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoOrganizer;

public sealed class OrganizeDownloadsTask : IScheduledTask
{
    private readonly OrganizerEngine _engine;
    private readonly ILogger<OrganizeDownloadsTask> _logger;

    public OrganizeDownloadsTask(IApplicationPaths paths, ILogger<OrganizeDownloadsTask> logger)
    {
        _logger = logger;
        _engine = new OrganizerEngine(Path.Combine(paths.DataPath, "autoorganizer"), entry =>
        {
            if (entry.Status == "Error") logger.LogWarning("AutoOrganizer {Status}: {Source} -> {Destination}: {Detail}", entry.Status, entry.Source, entry.Destination, entry.Detail);
            else if (entry.Status is "Imported" or "Preview" or "Skipped" or "Cleaned") logger.LogInformation("AutoOrganizer {Status}: {Source} -> {Destination}: {Detail}", entry.Status, entry.Source, entry.Destination, entry.Detail);
            else logger.LogDebug("AutoOrganizer {Status}: {Source}: {Detail}", entry.Status, entry.Source, entry.Detail);
        });
    }

    public string Name => "Organize incoming downloads";
    public string Key => "AutoOrganizerScan";
    public string Category => "Auto Organizer";
    public string Description => "Poll the incoming folder for completed movies and episodes; preview or copy/move them into the library.";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(1).Ticks }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var config = Plugin.Instance?.Configuration;
        if (config?.Enabled == true)
        {
            try
            {
                var report = await _engine.ScanAsync(config.ToSettings(), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("AutoOrganizer scan: {Imported} imported, {Previewed} previewed, {Errors} errors", report.Imported, report.Previewed, report.Errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AutoOrganizer scan failed. Check paths, permissions, and plugin settings.");
                throw;
            }
        }
        progress.Report(100);
    }
}
