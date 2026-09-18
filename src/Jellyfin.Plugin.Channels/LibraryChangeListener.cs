using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Channels;

/// <summary>
/// Rebuilds channels when the library moves underneath them.
///
/// This is not housekeeping — it is correctness. A running order is a sequence
/// of cumulative runtimes, so an item that has been deleted does not merely go
/// missing: every airing after it shifts. On a server with an automated cleanup
/// tool removing watched media, a channel left unrebuilt drifts a little further
/// out of step every day.
/// </summary>
public sealed class LibraryChangeListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelService _channels;
    private readonly ILogger<LibraryChangeListener> _logger;

    private readonly object _gate = new();
    private Timer? _debounce;

    /// <summary>
    /// A library scan emits events per item, and rebuilding on each would walk
    /// the library thousands of times for one scan. Coalesce into a single
    /// rebuild once the churn stops.
    /// </summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(30);

    public LibraryChangeListener(
        ILibraryManager libraryManager,
        ChannelService channels,
        ILogger<LibraryChangeListener> logger)
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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        return Task.CompletedTask;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        // Only things a channel can actually air are worth a rebuild; a changed
        // artist or box-set cover cannot move anyone's schedule. Episodes and
        // movies are both Video, which is exactly what the block resolver queries.
        if (e.Item is not Video)
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
