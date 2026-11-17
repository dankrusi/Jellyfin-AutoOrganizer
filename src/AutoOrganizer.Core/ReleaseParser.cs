using System.Text.RegularExpressions;

namespace AutoOrganizer.Core;

/// <summary>Conservative, offline release-name parsing. Unknown names stay in the inbox.</summary>
public static class ReleaseParser
{
    private static readonly Regex Episode = Rx(@"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]*e(?<e>\d{1,3})(?<more>(?:[ ._]*e\d{1,3})*)(?:[ ._]*-[ ._]*(?:s(?<ends>\d{1,2}))?e?(?<end>\d{1,3})(?![a-z0-9]))?(?!\d)");
    private static readonly Regex CrossEpisode = Rx(@"(?<!\w)(?<s>\d{1,2})x(?<e>\d{1,3})(?:-(?:(?<ends>\d{1,2})x)?(?<end>\d{1,3})(?![a-z0-9]))?(?!\d)");
    private static readonly Regex Season = Rx(@"^(?:season[ ._-]*|s)(?<s>\d{1,2})(?:$|[ ._-]*(?:\(|\[|\d{3,4}p\b))");
    private static readonly Regex Year = Rx(@"(?<!\d)(?:19|20)\d{2}(?!\d)");
    private static readonly Regex Noise = Rx(@"\b(?:season\s*\d|s\d{1,2}(?:\b|e)|\d{3,4}p\b|2160p|480p|1080i|720i|bluray|blu-ray|brrip|bdrip|web[ -]?(?:dl|rip)|hdtv|dvdrip|remux|x26[45]|h[ .]?26[45]|hevc|avc|aac|dts|eac3|ddp|complete|proper|repack)\b");
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        { "subs", "subtitles", "shows", "movies", "tv", "downloads", "incoming", "extras", "samples", "sample", "featurettes", "trailers", "behind the scenes", "deleted scenes" };

    public static MediaIdentity? Parse(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return null;
        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        if (parts.Any(p => Rx(@"^(?:extras|samples|sample|trailers|featurettes|behind the scenes|deleted scenes)$").IsMatch(p)) || Rx(@"(?:^|[ ._-])(?:sample|trailer)(?:$|[ ._-])").IsMatch(stem)) return null;
        var directories = parts.Take(parts.Length - 1).Reverse().ToArray();
        var match = Episode.Match(stem);
        if (!match.Success) match = CrossEpisode.Match(stem);
        if (match.Success)
        {
            // Do not silently discard a second season or a malformed episode suffix.
            var remainder = stem[(match.Index + match.Length)..];
            if (Rx(@"^[ ._-]*(?:s\d+e\d+|e\d+)").IsMatch(remainder)) return null;
            if (match.Groups["ends"].Success && int.Parse(match.Groups["ends"].Value) != int.Parse(match.Groups["s"].Value)) return null;
            var first = int.Parse(match.Groups["e"].Value);
            var last = match.Groups["end"].Success ? int.Parse(match.Groups["end"].Value) : first;
            var extra = Regex.Matches(match.Groups["more"].Value, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
            if (extra.Length > 0)
            {
                // E01E03 is not a contiguous range and cannot be renamed E01-E03.
                if (!extra.SequenceEqual(Enumerable.Range(first + 1, extra.Length))) return null;
                last = extra[^1];
            }
            if (last < first) return null;
            var title = CleanTitle(stem[..match.Index]);
            int? year = FindYear(stem[..match.Index]);
            if (title.Length == 0)
            {
                (title, year) = FindSeriesTitle(directories);
            }
            if (title.Length == 0) return null;
            return new(title, year, int.Parse(match.Groups["s"].Value), $"E{first:00}" + (last > first ? $"-E{last:00}" : ""));
        }

        var seasonDirectory = directories.Select(d => Season.Match(d)).FirstOrDefault(m => m.Success);
        if (seasonDirectory is not null)
        {
            var episodeOnly = Rx(@"^(?:e(?:pisode)?[ ._-]*)?(?<e>\d{1,3})(?:$|[ ._-])").Match(stem);
            if (!episodeOnly.Success) return null;
            var (title, year) = FindSeriesTitle(directories);
            return title.Length == 0 ? null : new(title, year, int.Parse(seasonDirectory.Groups["s"].Value), $"E{int.Parse(episodeOnly.Groups["e"].Value):00}");
        }

        // Season packs with unnumbered files must never be mistaken for movies.
        if (directories.Append(stem).Any(d => Rx(@"\b(?:season[ ._-]*\d|s\d{1,2}(?:\b|e\d)|\d{1,2}x\d)\b").IsMatch(d))) return null;
        if (Rx(@"\b(?:cd|disc|disk|part)[ ._-]*\d+\b").IsMatch(stem)) return null;
        foreach (var candidate in new[] { stem }.Concat(directories))
        {
            // Last year before release technical details handles titles such as
            // '2001 A Space Odyssey (1968)' and 'Blade Runner 2049 (2017)'.
            var noise = Noise.Match(candidate.Replace('.', ' ').Replace('_', ' '));
            var titleArea = noise.Success ? candidate[..noise.Index] : candidate;
            foreach (Match year in Year.Matches(titleArea).Reverse())
            {
                var title = CleanTitle(candidate[..year.Index], stripYear: false);
                if (title.Length > 0 && !Generic.Contains(title)) return new(title, int.Parse(year.Value), null, null);
            }
        }
        return null;
    }

    private static (string Title, int? Year) FindSeriesTitle(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            if (Season.IsMatch(directory) || Generic.Contains(directory)) continue;
            var title = CleanTitle(directory);
            if (title.Length > 0) return (title, FindYear(directory));
        }
        return ("", null);
    }

    private static int? FindYear(string text)
    {
        var year = Year.Matches(text).FirstOrDefault(m => m.Index > 0);
        return year is not null ? int.Parse(year.Value) : null;
    }

    private static string CleanTitle(string value, bool stripYear = true)
    {
        value = Regex.Replace(value, @"^(?:\[[^\]]+\]\s*)+", "");
        value = value.Replace('.', ' ').Replace('_', ' ');
        var year = Year.Matches(value).FirstOrDefault(m => m.Index > 0);
        if (stripYear && year is not null) value = value[..year.Index];
        var noise = Noise.Match(value);
        if (noise.Success) value = value[..noise.Index];
        value = value.Trim(' ', '-', '(', ')', '[', ']', '{', '}');
        return Regex.Replace(value, @"\s+", " ");
    }

    public static string SafeName(string value)
    {
        value = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1f]", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim().TrimEnd('.');
        if (value is "" or "." or "..") throw new ArgumentException("Empty media title.");
        if (Rx(@"^(?:con|prn|aux|nul|com\d|lpt\d)(?:\.|$)").IsMatch(value)) value = "_" + value;
        return value;
    }

    private static Regex Rx(string expression) => new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
