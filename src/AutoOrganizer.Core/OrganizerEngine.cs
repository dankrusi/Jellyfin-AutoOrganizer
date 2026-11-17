using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoOrganizer.Core;

public sealed record ScanEntry(string Source, string? Destination, string Status, string Detail);
public sealed record ScanReport(DateTime StartedUtc, List<ScanEntry> Entries)
{
    public int Imported { get; set; }
    public int Previewed { get; set; }
    public int Errors { get; set; }
}

/// <summary>Polling works across container bind mounts and recovers missed filesystem events.</summary>
public sealed class OrganizerEngine(string stateDirectory, Action<ScanEntry>? onEntry = null)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm" };
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".aac", ".ac3", ".eac3", ".dts", ".mka", ".flac" };
    private readonly Dictionary<string, Observation> _observations = new(PathComparer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Snapshot(long Length, DateTime ModifiedUtc);
    private sealed record Observation(Snapshot Snapshot, DateTime SinceUtc);
    public sealed record Receipt(string Source, long SourceLength, DateTime SourceModifiedUtc, string Destination, long DestinationLength, DateTime DestinationModifiedUtc);

    public async Task<ScanReport> ScanAsync(OrganizerSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ScanInternalAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ScanReport> ScanInternalAsync(OrganizerSettings settings, CancellationToken cancellationToken)
    {
        var report = new ScanReport(DateTime.UtcNow, []);
        void Record(string source, string? destination, string status, string detail)
        {
            var entry = new ScanEntry(source, destination, status, detail);
            if (report.Entries.Count < 500) report.Entries.Add(entry);
            if (status == "Imported") report.Imported++;
            if (status == "Preview") report.Previewed++;
            if (status == "Error") report.Errors++;
            onEntry?.Invoke(entry);
        }

        var incoming = RequireAbsolute(settings.IncomingPath);
        var library = string.IsNullOrWhiteSpace(settings.LibraryPath) ? null : RequireAbsolute(settings.LibraryPath);
        if (library is null && (string.IsNullOrWhiteSpace(settings.ShowsPath) || string.IsNullOrWhiteSpace(settings.MoviesPath)))
            throw new ArgumentException("Set a shared library root, or select both Shows and Movies destinations.");
        var shows = string.IsNullOrWhiteSpace(settings.ShowsPath) ? Path.Combine(library!, "Shows") : RequireAbsolute(settings.ShowsPath);
        var movies = string.IsNullOrWhiteSpace(settings.MoviesPath) ? Path.Combine(library!, "Movies") : RequireAbsolute(settings.MoviesPath);
        var libraryRoots = library is null ? new[] { shows, movies } : new[] { library, shows, movies };
        if (libraryRoots.Any(root => Within(incoming, root) || Within(root, incoming)))
            throw new ArgumentException("Incoming and library folders must be separate, with neither inside the other.");
        if (Within(shows, movies) || Within(movies, shows)) throw new ArgumentException("Shows and Movies destinations must be separate, with neither inside the other.");
        if (!Directory.Exists(incoming)) throw new DirectoryNotFoundException($"Incoming folder does not exist: {incoming}");
        if (settings.StableSeconds < 1) throw new ArgumentException("StableSeconds must be at least 1.");
        if (!Enum.IsDefined(settings.Mode)) throw new ArgumentException("Invalid import mode.");
        var marker = settings.CompletionMarker.Trim();
        if (marker.Length > 0 && (marker is "." or ".." || marker.IndexOfAny(['/', '\\']) >= 0)) throw new ArgumentException("Completion marker must be a filename, not a path.");
        EnsureNoLinks(incoming);
        foreach (var root in libraryRoots) EnsureNoLinks(root);
        var state = Path.GetFullPath(stateDirectory);
        if (Within(state, incoming) || libraryRoots.Any(root => Within(state, root))) throw new ArgumentException("State directory must be outside incoming and library folders.");
        Directory.CreateDirectory(state);
        EnsureNoLinks(state);
        var receiptsPath = Path.Combine(state, "receipts.json");
        EnsureNoLinks(receiptsPath);
        var receipts = File.Exists(receiptsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, Receipt>>(await File.ReadAllTextAsync(receiptsPath, cancellationToken).ConfigureAwait(false)) ?? throw new InvalidDataException("Invalid receipt ledger.")
            : new Dictionary<string, Receipt>();

        // Never follow reparse points, including symlinked media files and directories.
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        var files = Directory.GetFiles(incoming, "*", enumeration);
        // Observe hidden/zero-byte release attachments too, so Move can preserve
        // the entire release after its media has been imported successfully.
        var allFiles = Directory.GetFiles(incoming, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint });
        var seen = new HashSet<string>(allFiles, PathComparer);
        foreach (var stale in _observations.Keys.Where(p => !seen.Contains(p)).ToArray()) _observations.Remove(stale);
        var now = DateTime.UtcNow;
        foreach (var file in allFiles)
        {
            var snapshot = ReadSnapshot(file);
            if (!_observations.TryGetValue(file, out var previous) || previous.Snapshot != snapshot)
                _observations[file] = new(snapshot, now);
        }

        var blockedPacks = allFiles.Where(IsPartial).Select(f => DownloadUnit(incoming, f)).ToHashSet(PathComparer);
        var byDirectory = files.GroupBy(f => Path.GetDirectoryName(f)!, PathComparer).ToDictionary(g => g.Key, g => g.ToArray(), PathComparer);
        foreach (var video in files.Where(f => VideoExtensions.Contains(Path.GetExtension(f))).OrderBy(p => p, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? destination = null;
            try
            {
                var relative = Path.GetRelativePath(incoming, video);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('@') || p.StartsWith('_') || p.StartsWith('.'))) continue;
                var unit = DownloadUnit(incoming, video);
                if (blockedPacks.Contains(unit)) { Record(video, null, "Waiting", "Download has partial files in its download folder."); continue; }
                if (marker.Length > 0 && (unit == incoming || !File.Exists(Path.Combine(unit, marker))))
                { Record(video, null, "Waiting", "Completion marker missing; marker mode requires a top-level download folder."); continue; }
                var identity = ReleaseParser.Parse(relative);
                if (identity is null) { Record(video, null, "Skipped", "Cannot confidently identify title/year or season/episode."); continue; }
                var destinationRoot = identity.IsSeries ? shows : movies;
                destination = Path.Combine(destinationRoot, identity.RelativeDestination(Path.GetExtension(video), settings.IncludeSeriesYear, includeCategory: false));
                if (!Within(destination, destinationRoot)) throw new IOException("Destination escapes library root.");
                var sourceStem = Path.GetFileNameWithoutExtension(video);
                var destinationStem = Path.GetFileNameWithoutExtension(destination);
                var sidecars = byDirectory[Path.GetDirectoryName(video)!]
                    .Where(f => SidecarExtensions.Contains(Path.GetExtension(f)))
                    .Where(f => Path.GetFileNameWithoutExtension(f).Equals(sourceStem, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith(sourceStem + ".", StringComparison.OrdinalIgnoreCase))
                    .Select(f => (Source: f, Destination: Path.Combine(Path.GetDirectoryName(destination)!, destinationStem + Path.GetFileName(f)[sourceStem.Length..])));
                // Publish sidecars first and video last. A failed transfer never deletes sources.
                var group = sidecars.Append((Source: video, Destination: destination)).ToArray();
                if (group.Any(p => !IsStable(p.Source, now, settings.StableSeconds)))
                { Record(video, destination, "Waiting", "Waiting for unchanged size and modification time across polls."); continue; }
                foreach (var pair in group)
                {
                    EnsureNoLinks(pair.Source);
                    EnsureNoLinks(pair.Destination);
                    if (File.Exists(pair.Destination) && !HasReceipt(pair.Source, pair.Destination, receipts))
                        throw new IOException($"Destination already exists; never overwrite: {pair.Destination}");
                }
                if (settings.DryRun)
                {
                    if (group.Any(p => !HasReceipt(p.Source, p.Destination, receipts)))
                        Record(video, destination, "Preview", $"Would {settings.Mode.ToString().ToLowerInvariant()} video and {group.Length - 1} sidecar(s)." +
                            (settings.Mode == ImportMode.Move && unit != incoming ? $" Remaining release files will be preserved under {Path.Combine(ContentFolder(identity, destination), ".download-extras", ReleaseParser.SafeName(Path.GetFileName(unit)))} and empty source folders removed after the release finishes." : ""));
                    continue;
                }
                var imported = false;
                foreach (var pair in group)
                {
                    if (HasReceipt(pair.Source, pair.Destination, receipts)) continue;
                    var receipt = await PublishAsync(pair.Source, pair.Destination, _observations[pair.Source].Snapshot, cancellationToken).ConfigureAwait(false);
                    receipts[ReceiptKey(pair.Source, pair.Destination)] = receipt;
                    await WriteJsonAsync(receiptsPath, receipts, cancellationToken).ConfigureAwait(false);
                    imported = true;
                }
                if (settings.Mode == ImportMode.Move)
                {
                    // Check the entire group before removing anything, and again per file.
                    if (group.Any(p => !HasReceipt(p.Source, p.Destination, receipts))) throw new IOException("Source changed after import; keeping source files.");
                    foreach (var pair in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EnsureNoLinks(pair.Source);
                        if (!HasReceipt(pair.Source, pair.Destination, receipts)) throw new IOException("Source changed; keeping source file.");
                        File.Delete(pair.Source);
                        _observations.Remove(pair.Source);
                    }
                    imported = true;
                }
                if (imported) Record(video, destination, "Imported", $"{settings.Mode}: video and matching sidecars.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                Record(video, destination, "Error", ex.Message);
            }
        }
        if (settings.Mode == ImportMode.Move && !settings.DryRun)
            await FinishReleaseFoldersAsync(incoming, shows, movies, settings, receipts, receiptsPath, now, Record, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(state, "last-run.json"), report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private bool IsStable(string path, DateTime now, int seconds, bool allowEmpty = false) =>
        _observations.TryGetValue(path, out var observation) && (allowEmpty || observation.Snapshot.Length > 0) &&
        now - observation.SinceUtc >= TimeSpan.FromSeconds(seconds) &&
        now - observation.Snapshot.ModifiedUtc >= TimeSpan.FromSeconds(seconds) &&
        observation.Snapshot == ReadSnapshot(path);

    private static string ContentFolder(MediaIdentity identity, string destination) => identity.IsSeries
        ? Path.GetDirectoryName(Path.GetDirectoryName(destination))!
        : Path.GetDirectoryName(destination)!;

    private async Task FinishReleaseFoldersAsync(string incoming, string shows, string movies, OrganizerSettings settings,
        Dictionary<string, Receipt> receipts, string receiptsPath, DateTime now,
        Action<string, string?, string, string> record, CancellationToken cancellationToken)
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        // Receipts survive restarts, including a failure after the final video was
        // moved but before its release attachments were transferred.
        var anchors = new List<(string Unit, string Content)>();
        foreach (var receipt in receipts.Values.ToArray())
        {
            if (!Within(receipt.Source, incoming) || !VideoExtensions.Contains(Path.GetExtension(receipt.Source))) continue;
            var unit = DownloadUnit(incoming, receipt.Source);
            if (unit == incoming || !Directory.Exists(unit)) continue;
            var identity = ReleaseParser.Parse(Path.GetRelativePath(incoming, receipt.Source));
            if (identity is null) continue;
            var expected = Path.Combine(identity.IsSeries ? shows : movies, identity.RelativeDestination(Path.GetExtension(receipt.Source), settings.IncludeSeriesYear, includeCategory: false));
            if (!expected.Equals(receipt.Destination, PathComparison) || !File.Exists(expected)) continue;
            if (ReadSnapshot(expected) != new Snapshot(receipt.DestinationLength, receipt.DestinationModifiedUtc)) continue;
            anchors.Add((unit, ContentFolder(identity, expected)));
        }

        foreach (var release in anchors.GroupBy(a => a.Unit, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = release.Key;
            try
            {
                EnsureNoLinks(unit);
                var targets = release.Select(a => a.Content).Distinct(PathComparer).ToArray();
                if (targets.Length != 1)
                {
                    record(unit, null, "Waiting", "Release contains multiple titles; keeping remaining files because a single destination is ambiguous.");
                    continue;
                }
                var remaining = Directory.GetFiles(unit, "*", enumeration);
                if (settings.CompletionMarker.Trim().Length > 0 && !File.Exists(Path.Combine(unit, settings.CompletionMarker.Trim())))
                { record(unit, null, "Waiting", "Completion marker missing; keeping the release folder and attachments."); continue; }
                if (remaining.Any(IsPartial)) { record(unit, null, "Waiting", "Release still has partial-download files; keeping its folder."); continue; }
                // Unknown main videos stay for review. Known samples/extras are
                // preserved with the other release files, not exposed as movies.
                if (remaining.Any(f => VideoExtensions.Contains(Path.GetExtension(f)) && !IsReleaseExtraVideo(Path.GetRelativePath(unit, f))))
                { record(unit, null, "Waiting", "Release still has unimported video files; keeping its folder and attachments."); continue; }
                if (remaining.Any(f => !IsStable(f, now, settings.StableSeconds, allowEmpty: true)))
                { record(unit, null, "Waiting", "Waiting for the remaining release files to stop changing before moving them."); continue; }

                var archiveRoot = Path.Combine(targets[0], ".download-extras", ReleaseParser.SafeName(Path.GetFileName(unit)));
                var transfers = remaining.Select(source => (Source: source, Destination: Path.Combine(archiveRoot, Path.GetRelativePath(unit, source)))).ToArray();
                foreach (var pair in transfers)
                {
                    EnsureNoLinks(pair.Source);
                    EnsureNoLinks(pair.Destination);
                    if (!Within(pair.Destination, archiveRoot)) throw new IOException("Release attachment escapes its destination.");
                    if (File.Exists(pair.Destination) && !HasReceipt(pair.Source, pair.Destination, receipts)) throw new IOException($"Release attachment already exists; never overwrite: {pair.Destination}");
                }
                if (transfers.Length > 0)
                {
                    // Exclude the attachment tree, including samples, from scans.
                    var ignore = Path.Combine(targets[0], ".download-extras", ".ignore");
                    EnsureNoLinks(ignore);
                    Directory.CreateDirectory(Path.GetDirectoryName(ignore)!);
                    if (File.Exists(ignore))
                    {
                        if (new FileInfo(ignore).Length != 0) throw new IOException("Release attachment .ignore file is not empty; keeping sources for review.");
                    }
                    else
                    {
                        using var ignoreStream = new FileStream(ignore, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        ignoreStream.Flush(flushToDisk: true);
                    }
                }
                foreach (var pair in transfers)
                {
                    if (HasReceipt(pair.Source, pair.Destination, receipts)) continue;
                    var receipt = await PublishAsync(pair.Source, pair.Destination, _observations[pair.Source].Snapshot, cancellationToken).ConfigureAwait(false);
                    receipts[ReceiptKey(pair.Source, pair.Destination)] = receipt;
                    await WriteJsonAsync(receiptsPath, receipts, cancellationToken).ConfigureAwait(false);
                }
                if (transfers.Any(p => !HasReceipt(p.Source, p.Destination, receipts))) throw new IOException("Release attachment changed after transfer; keeping source files.");
                foreach (var pair in transfers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureNoLinks(pair.Source);
                    if (!HasReceipt(pair.Source, pair.Destination, receipts)) throw new IOException("Release attachment changed; keeping source file.");
                    File.Delete(pair.Source);
                    _observations.Remove(pair.Source);
                }
                // No recursive deletion: links, locked files, or newly arriving
                // files keep their directory in place. Never remove the inbox.
                foreach (var directory in Directory.GetDirectories(unit, "*", enumeration).OrderByDescending(d => d.Length).Append(unit))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (directory == incoming || !Within(directory, unit)) throw new IOException("Refusing to remove a directory outside this release.");
                    EnsureNoLinks(directory);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
                }
                record(unit, archiveRoot, Directory.Exists(unit) ? "Waiting" : "Cleaned",
                    Directory.Exists(unit) ? "Preserved release files; source folder still contains links or newly arriving files and was kept." : $"Preserved {transfers.Length} remaining release file(s) and removed empty source folders.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { record(unit, null, "Error", "Release folder cleanup: " + ex.Message); }
        }
    }

    private static bool IsReleaseExtraVideo(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        return parts.Any(p => p.StartsWith('.') || p.StartsWith('@') || p.StartsWith('_') ||
                new[] { "extras", "samples", "sample", "trailers", "featurettes", "behind the scenes", "deleted scenes" }.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(relativePath), @"(?:^|[ ._-])(?:sample|trailer)(?:$|[ ._-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool HasReceipt(string source, string destination, Dictionary<string, Receipt> receipts)
    {
        if (!receipts.TryGetValue(ReceiptKey(source, destination), out var receipt) || !File.Exists(destination)) return false;
        var src = ReadSnapshot(source);
        var dst = ReadSnapshot(destination);
        return src == new Snapshot(receipt.SourceLength, receipt.SourceModifiedUtc) && dst == new Snapshot(receipt.DestinationLength, receipt.DestinationModifiedUtc);
    }

    private static async Task<Receipt> PublishAsync(string source, string destination, Snapshot expected, CancellationToken cancellationToken)
    {
        EnsureNoLinks(source);
        EnsureNoLinks(destination);
        var before = ReadSnapshot(source);
        if (before != expected) throw new IOException("Source changed since stability check; waiting for a later poll.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNoLinks(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".autoorganizer-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (output.Length != before.Length || before != ReadSnapshot(source)) throw new IOException("Source changed during transfer; retrying on a later poll.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(destination);
            File.Move(temporary, destination, overwrite: false);
            var after = ReadSnapshot(destination);
            return new(source, before.Length, before.ModifiedUtc, destination, after.Length, after.ModifiedUtc);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Snapshot ReadSnapshot(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Source or destination disappeared.", path);
        return new(info.Length, info.LastWriteTimeUtc);
    }

    private static string ReceiptKey(string source, string destination) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + destination)));
    private static bool IsPartial(string path) => new[] { ".part", ".partial", ".crdownload", ".!qb", ".!ut", ".aria2", ".download", ".tmp" }.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    private static string DownloadUnit(string incoming, string path)
    {
        var relative = Path.GetRelativePath(incoming, path);
        var firstSeparator = relative.IndexOf(Path.DirectorySeparatorChar);
        return firstSeparator < 0 ? incoming : Path.Combine(incoming, relative[..firstSeparator]);
    }

    private static string RequireAbsolute(string path) => Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new ArgumentException("Configure absolute paths as seen by the Jellyfin server/container.");
    private static bool Within(string candidate, string parent) => candidate.Equals(parent, PathComparison) || candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);

    private static void EnsureNoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links/reparse points are not supported: {cursor}");
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
