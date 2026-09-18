using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Channels;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>User-defined channels. Empty until one is created.</summary>
    public List<ChannelDefinition> Channels { get; set; } = [];
}
