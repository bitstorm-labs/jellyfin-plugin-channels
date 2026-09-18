using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Channels;

public class ChannelsPlugin : BasePlugin<PluginConfiguration>
{
    public ChannelsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static ChannelsPlugin? Instance { get; private set; }

    public override string Name => "Channels";

    public override Guid Id => Guid.Parse("7a3f1c92-5d64-4b08-9f21-6c8e5b0a4d73");

    public override string Description =>
        "Builds 24/7 virtual channels from your own library. A channel runs on the "
        + "wall clock whether or not anyone is watching, so tuning in joins whatever "
        + "is airing rather than resuming where you left off.";
}
