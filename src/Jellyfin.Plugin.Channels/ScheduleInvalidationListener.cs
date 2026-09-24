using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Rebuilds channels when anything the schedule is derived from changes —
/// the library underneath them, or the definitions themselves.
///
/// This is not housekeeping — it is correctness. A running order is a sequence
/// of cumulative runtimes, so an item that has been deleted does not merely go
/// missing: every airing after it shifts. On a server with an automated cleanup
/// tool removing watched media, a channel left unrebuilt drifts a little further
/// out of step every day.
/// </summary>
public sealed class ScheduleInvalidationListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelService _channels;
    private readonly ILogger<ScheduleInvalidationListener> _logger;

    private readonly object _gate = new();
    private Timer? _debounce;

    /// <summary>
    /// A library scan emits events per item, and rebuilding on each would walk
    /// the library thousands of times for one scan. Coalesce into a single
    /// rebuild once the churn stops.
    /// </summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(30);

    public ScheduleInvalidationListener(
        ILibraryManager libraryManager,
        ChannelService channels,
        ILogger<ScheduleInvalidationListener> logger)
    {
        _libraryManager = libraryManager;
        _channels = channels;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;

        // A definition edited in the dashboard changes the schedule as surely as
        // a deleted episode does, and the cache cannot notice on its own — the
        // configuration object carries no version to compare against. Without
        // this the edit is persisted correctly and then ignored until something
        // unrelated happens to invalidate, so a new daypart, block or season is
        // saved and simply does not take effect. That failure is particularly
        // nasty because the dashboard reports success.
        if (ChannelsPlugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;

        if (ChannelsPlugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuild at once rather than after the debounce the library uses. A config
    /// edit is a single deliberate act, not the thousands of events a scan
    /// emits, and whoever made it is looking at the result right now.
    /// </summary>
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        try
        {
            _channels.Invalidate();
            _logger.LogInformation("Channel definitions changed; running orders will be rebuilt on next tune-in");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate channel snapshots after a configuration change");
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        // Only things that can move a schedule are worth a rebuild: a changed
        // artist cannot. Episodes and movies are both Video, which is what the
        // block resolver queries — and a collection, because a Collection block
        // is exactly its membership. Adding a film to one updates the BoxSet,
        // not any Video, and with the old Video-only test the channel kept its
        // cached order until something unrelated happened to invalidate it.
        // Seen live: eight films added to a collection, none on air until a
        // config re-save forced the rebuild.
        if (e.Item is not (Video or BoxSet))
        {
            return;
        }

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Rebuild(), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Rebuild()
    {
        try
        {
            _channels.Invalidate();
            _logger.LogInformation("Library changed; channel running orders will be rebuilt on next tune-in");
        }
        catch (Exception ex)
        {
            // A timer callback that throws takes the process down.
            _logger.LogError(ex, "Failed to invalidate channel snapshots after a library change");
        }
    }
}
