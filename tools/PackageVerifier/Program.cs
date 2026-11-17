using System.IO.Compression;
using System.Reflection;

if (args.Length != 3)
    throw new ArgumentException("Usage: PackageVerifier ARCHIVE VERSION SOURCE_SETTINGS_HTML");

var parsedVersion = Version.Parse(args[1]);
var expectedVersion = new Version(parsedVersion.Major, parsedVersion.Minor, Math.Max(0, parsedVersion.Build), Math.Max(0, parsedVersion.Revision));
using var archive = ZipFile.OpenRead(args[0]);
byte[] ReadEntry(string filename)
{
    var matches = archive.Entries.Where(entry => entry.Name == filename).ToArray();
    if (matches.Length != 1) throw new InvalidDataException($"Expected exactly one {filename} in package.");
    using var input = matches[0].Open();
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
}

var plugin = Assembly.Load(ReadEntry("Jellyfin.Plugin.AutoOrganizer.dll"));
var core = Assembly.Load(ReadEntry("AutoOrganizer.Core.dll"));
foreach (var assembly in new[] { plugin, core })
    if (assembly.GetName().Version != expectedVersion)
        throw new InvalidDataException($"Incorrect {assembly.GetName().Name} version: {assembly.GetName().Version}; expected {expectedVersion}.");

using var resource = plugin.GetManifestResourceStream("Jellyfin.Plugin.AutoOrganizer.Configuration.configPage.html")
    ?? throw new InvalidDataException("Settings page is not embedded in the plugin.");
using var resourceBytes = new MemoryStream();
resource.CopyTo(resourceBytes);
if (!resourceBytes.ToArray().SequenceEqual(File.ReadAllBytes(args[2])))
    throw new InvalidDataException("Packaged settings page does not match release source.");

var text = System.Text.Encoding.UTF8.GetString(resourceBytes.ToArray());
foreach (var control in new[] { "ShowsLibrary", "MoviesLibrary", "BrowseIncomingPath", "FolderBrowser" })
    if (!text.Contains($"id=\"{control}\"", StringComparison.Ordinal)) throw new InvalidDataException($"Packaged settings are missing {control}.");
Console.WriteLine($"Package verified: both DLLs are {expectedVersion}; embedded settings match source and contain library selectors and folder browser.");
