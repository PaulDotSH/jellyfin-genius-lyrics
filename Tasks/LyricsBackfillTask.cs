using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GeniusLyricsPlugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace GeniusLyricsPlugin.Tasks
{
    public class LyricsBackfillTask : IScheduledTask
    {
        private readonly ILogger<LyricsBackfillTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly ScraperService _scraperService;
        private readonly IDirectoryService _directoryService;
        private readonly IApplicationPaths _applicationPaths;

        public LyricsBackfillTask(
            ILogger<LyricsBackfillTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IApplicationPaths applicationPaths)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _applicationPaths = applicationPaths;
            _scraperService = new ScraperService(new LoggerFactory().CreateLogger<ScraperService>());
        }

        public string Name => "Genius Lyrics Backfill";

        public string Key => "GeniusLyricsBackfillTask";

        public string Description => "Backfills missing lyrics for all audio items by scraping Genius and saving as .txt files.";

        public string Category => "Lyrics";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableGenius)
            {
                _logger.LogInformation("Genius lyrics are disabled in configuration. Skipping backfill.");
                return;
            }

            _logger.LogInformation("Starting Genius Lyrics Backfill task...");

            var cachePath = Path.Combine(_applicationPaths.PluginConfigurationsPath, "GeniusLyricsBackoffCache.json");
            Dictionary<Guid, DateTime> backoffCache = new Dictionary<Guid, DateTime>();

            if (File.Exists(cachePath))
            {
                try
                {
                    var cacheJson = await File.ReadAllTextAsync(cachePath, cancellationToken);
                    backoffCache = JsonSerializer.Deserialize<Dictionary<Guid, DateTime>>(cacheJson) ?? new Dictionary<Guid, DateTime>();
                    _logger.LogInformation($"Loaded {backoffCache.Count} items from backoff cache.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load backoff cache.");
                }
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-30);

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsVirtualItem = false
            };

            var items = _libraryManager.GetItemList(query)
                .OfType<Audio>()
                .Where(a => a.HasLyrics != true)
                .Where(a => !backoffCache.TryGetValue(a.Id, out var lastChecked) || lastChecked < cutoffDate)
                .ToList();
            _logger.LogInformation($"Found {items.Count} audio items missing lyrics to check (after backoff filtering).");

            if (items.Count == 0)
            {
                progress.Report(100);
                return;
            }

            int processedCount = 0;
            int successCount = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var title = item.Name;
                    var artist = item.Artists.FirstOrDefault() ?? item.AlbumArtists.FirstOrDefault();

                    bool lyricsFound = false;
                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                    {
                        var url = await _scraperService.SearchSongUrlAsync(artist, title, config.GeniusApiKey, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var lyrics = await _scraperService.ScrapeLyricsAsync(url, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(lyrics))
                            {
                                lyricsFound = true;
                                if (config.SaveLyricsInMediaFolder && !string.IsNullOrWhiteSpace(item.Path))
                                {
                                    var txtPath = Path.ChangeExtension(item.Path, ".txt");
                                    await File.WriteAllTextAsync(txtPath, lyrics, cancellationToken);
                                    _logger.LogInformation($"Saved lyrics for '{artist} - {title}' to {txtPath}");
                                    
                                    // Trigger a metadata refresh so Jellyfin detects the new sidecar file
                                    _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(_directoryService)
                                    {
                                        MetadataRefreshMode = MetadataRefreshMode.Default,
                                        ForceSave = false
                                    }, RefreshPriority.Normal);
                                    
                                    successCount++;
                                }
                                else
                                {
                                    _logger.LogWarning($"SaveLyricsInMediaFolder is disabled or item path is missing for '{artist} - {title}'. Skipping save.");
                                }
                            }
                        }
                    }
                    
                    if (lyricsFound)
                    {
                        if (backoffCache.ContainsKey(item.Id))
                        {
                            backoffCache.Remove(item.Id);
                        }
                    }
                    else
                    {
                        backoffCache[item.Id] = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error backfilling lyrics for {item.Name}");
                }

                processedCount++;
                double percent = (double)processedCount / items.Count * 100;
                progress.Report(percent);
                
                // Add a small delay to avoid rate limiting from Genius
                await Task.Delay(1000, cancellationToken);
            }

            try
            {
                var cacheJson = JsonSerializer.Serialize(backoffCache);
                await File.WriteAllTextAsync(cachePath, cacheJson, cancellationToken);
                _logger.LogInformation("Saved backoff cache.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save backoff cache.");
            }

            _logger.LogInformation($"Genius Lyrics Backfill task completed. Successfully downloaded and saved {successCount} lyrics.");
            progress.Report(100);
        }
    }
}
