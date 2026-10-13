using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GeniusLyricsPlugin.Configuration;
using GeniusLyricsPlugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Tasks
{
    public class LyricsBackfillTask : IScheduledTask
    {
        // Several failed requests in a row means we're blocked or offline; carrying on would just burn through the library.
        private const int MaxConsecutiveFailures = 5;
        private const int SaveCacheEvery = 25;
        private const int InstrumentalBackoffDays = 365;

        private readonly ILogger<LyricsBackfillTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly ILyricManager _lyricManager;
        private readonly ScraperService _scraperService;
        private readonly LrcLibClient _lrcLibClient;
        private readonly IDirectoryService _directoryService;
        private readonly IApplicationPaths _applicationPaths;

        public LyricsBackfillTask(
            ILogger<LyricsBackfillTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            ILyricManager lyricManager,
            ScraperService scraperService,
            LrcLibClient lrcLibClient,
            IDirectoryService directoryService,
            IApplicationPaths applicationPaths)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _lyricManager = lyricManager;
            _scraperService = scraperService;
            _lrcLibClient = lrcLibClient;
            _directoryService = directoryService;
            _applicationPaths = applicationPaths;
        }

        private enum ItemOutcome
        {
            Saved,
            NotFound,
            Instrumental,
            Failed
        }

        public string Name => "Genius Lyrics Backfill";

        public string Key => "GeniusLyricsBackfillTask";

        public string Description => "Backfills missing lyrics for all audio items from Genius (and LRCLIB, if enabled).";

        public string Category => "Lyrics";

        private string CachePath => Path.Combine(_applicationPaths.PluginConfigurationsPath, "GeniusLyricsBackoffCache.json");

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.WeeklyTrigger,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.CurrentConfiguration;
            if (!config.EnableGenius && !config.EnableFallback)
            {
                _logger.LogInformation("Genius and the LRCLIB fallback are both disabled. Skipping backfill.");
                return;
            }

            // Maps item id to the earliest time it should be looked up again.
            var retryAfter = await LoadBackoffCacheAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsVirtualItem = false
            };

            var items = _libraryManager.GetItemList(query)
                .OfType<Audio>()
                .Where(a => a.HasLyrics != true)
                .Where(a => !retryAfter.TryGetValue(a.Id, out var next) || next <= now)
                .ToList();
            _logger.LogInformation("Found {Count} audio items missing lyrics to check (after backoff filtering).", items.Count);

            int processedCount = 0;
            int savedCount = 0;
            int consecutiveFailures = 0;

            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ItemOutcome outcome;
                    try
                    {
                        outcome = await ProcessItemAsync(item, config, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error backfilling lyrics for {Name}", item.Name);
                        outcome = ItemOutcome.Failed;
                    }

                    switch (outcome)
                    {
                        case ItemOutcome.Saved:
                            savedCount++;
                            retryAfter.Remove(item.Id);
                            break;
                        case ItemOutcome.NotFound:
                            retryAfter[item.Id] = DateTime.UtcNow.AddDays(Math.Max(1, config.BackoffDays));
                            break;
                        case ItemOutcome.Instrumental:
                            retryAfter[item.Id] = DateTime.UtcNow.AddDays(InstrumentalBackoffDays);
                            break;
                    }

                    consecutiveFailures = outcome == ItemOutcome.Failed ? consecutiveFailures + 1 : 0;

                    processedCount++;
                    progress.Report((double)processedCount / items.Count * 100);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _logger.LogWarning(
                            "Stopping the backfill after {Count} failed lookups in a row; Genius is probably blocking or rate limiting this server. Unchecked items will be retried next run.",
                            consecutiveFailures);
                        break;
                    }

                    if (processedCount % SaveCacheEvery == 0)
                    {
                        await SaveBackoffCacheAsync(retryAfter).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Saved even when cancelled, so an interrupted run doesn't redo everything it already checked.
                await SaveBackoffCacheAsync(retryAfter).ConfigureAwait(false);
            }

            _logger.LogInformation("Genius Lyrics Backfill completed. Checked {Processed} items and saved lyrics for {Saved}.", processedCount, savedCount);
            progress.Report(100);
        }

        private async Task<ItemOutcome> ProcessItemAsync(Audio item, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var title = item.Name;
            var artists = item.Artists.Count > 0 ? item.Artists : item.AlbumArtists;
            if (string.IsNullOrWhiteSpace(title) || artists.Count == 0)
            {
                return ItemOutcome.NotFound;
            }

            var anyFailed = false;
            if (config.EnableGenius)
            {
                var genius = await _scraperService.FindLyricsAsync(artists, title, cancellationToken).ConfigureAwait(false);
                switch (genius.Status)
                {
                    case LyricsLookupStatus.Found:
                        return await SaveLyricsAsync(item, genius, cancellationToken).ConfigureAwait(false);
                    case LyricsLookupStatus.Instrumental:
                        return ItemOutcome.Instrumental;
                    case LyricsLookupStatus.Failed:
                        anyFailed = true;
                        break;
                }
            }

            if (config.EnableFallback)
            {
                var duration = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : (double?)null;
                var lrcLib = await _lrcLibClient.FindLyricsAsync(artists, title, duration, cancellationToken).ConfigureAwait(false);
                switch (lrcLib.Status)
                {
                    case LyricsLookupStatus.Found:
                        return await SaveLyricsAsync(item, lrcLib, cancellationToken).ConfigureAwait(false);
                    case LyricsLookupStatus.Instrumental:
                        return ItemOutcome.Instrumental;
                    case LyricsLookupStatus.Failed:
                        anyFailed = true;
                        break;
                }
            }

            return anyFailed ? ItemOutcome.Failed : ItemOutcome.NotFound;
        }

        private async Task<ItemOutcome> SaveLyricsAsync(Audio item, LyricsLookupResult lyrics, CancellationToken cancellationToken)
        {
            if (Plugin.CurrentConfiguration.SaveLyricsInMediaFolder && !string.IsNullOrWhiteSpace(item.Path))
            {
                var path = Path.ChangeExtension(item.Path, "." + lyrics.Format);
                try
                {
                    if (File.Exists(path))
                    {
                        // Most likely written by an earlier run before Jellyfin picked it up; don't clobber it, just rescan.
                        _logger.LogInformation("{Path} already exists; refreshing the item instead of overwriting it", path);
                    }
                    else
                    {
                        await File.WriteAllTextAsync(path, lyrics.Lyrics, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("Saved lyrics for {Name} to {Path}", item.Name, path);
                    }

                    QueueRefresh(item);
                    return ItemOutcome.Saved;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Read-only media folders are common in containers; Jellyfin can still keep the lyrics in its metadata folder.
                    _logger.LogWarning("Could not write {Path} ({Error}); letting Jellyfin store the lyrics instead", path, ex.Message);
                }
            }

            var saved = await _lyricManager.SaveLyricAsync(item, lyrics.Format, lyrics.Lyrics!).ConfigureAwait(false);
            if (saved == null)
            {
                _logger.LogWarning("Jellyfin could not parse or save the lyrics found for {Name}", item.Name);
                return ItemOutcome.Failed;
            }

            _logger.LogInformation("Saved lyrics for {Name} through Jellyfin", item.Name);
            QueueRefresh(item);
            return ItemOutcome.Saved;
        }

        // Makes Jellyfin rescan the item so it detects the new lyric file.
        private void QueueRefresh(Audio item)
        {
            _providerManager.QueueRefresh(
                item.Id,
                new MetadataRefreshOptions(_directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ForceSave = false
                },
                RefreshPriority.Normal);
        }

        private async Task<Dictionary<Guid, DateTime>> LoadBackoffCacheAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(CachePath))
            {
                return new Dictionary<Guid, DateTime>();
            }

            try
            {
                var json = await File.ReadAllTextAsync(CachePath, cancellationToken).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(json) ?? new Dictionary<Guid, DateTime>();
                _logger.LogInformation("Loaded {Count} items from backoff cache.", cache.Count);
                return cache;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                _logger.LogError(ex, "Failed to load backoff cache.");
                return new Dictionary<Guid, DateTime>();
            }
        }

        private async Task SaveBackoffCacheAsync(Dictionary<Guid, DateTime> cache)
        {
            try
            {
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(cache), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to save backoff cache.");
            }
        }
    }
}
