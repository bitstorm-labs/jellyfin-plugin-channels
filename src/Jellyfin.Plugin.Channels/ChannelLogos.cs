using System.Reflection;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// The logos the plugin ships, compiled in as resources so a channel's logo
/// needs no file on the server and every client gets the same one. Each key
/// has a colour version and a white "-mono" one for the on-screen corner logo.
/// </summary>
public static class ChannelLogos
{
    private const string Prefix = "Jellyfin.Plugin.Channels.Logos.";

    private static readonly Assembly Assembly = typeof(ChannelLogos).Assembly;

    /// <summary>Every logo key, for the dashboard's picker.</summary>
    public static IReadOnlyList<string> Keys { get; } = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..^".png".Length])
        .Where(k => !k.EndsWith("-mono", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The PNG for a key, or null when there is no such logo.</summary>
    public static Stream? Open(string key, bool mono)
    {
        // Keys are a closed set; anything else is refused rather than used
        // to build a resource name.
        if (!Keys.Contains(key))
        {
            return null;
        }

        return Assembly.GetManifestResourceStream(Prefix + key + (mono ? "-mono" : string.Empty) + ".png");
    }
}
