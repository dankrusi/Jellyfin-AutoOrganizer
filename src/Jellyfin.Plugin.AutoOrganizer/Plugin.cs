using Jellyfin.Plugin.AutoOrganizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AutoOrganizer;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "Auto Organizer";
    public override string Description => "Organize completed movie and TV downloads into Jellyfin library folders.";
    public override Guid Id => Guid.Parse("684d73d5-e50e-4e59-b643-d9d288531cce");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "AutoOrganizer-" + GetType().Assembly.GetName().Version, EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }
    ];
}
