using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.MediaServers.Plex;
using Ombi.Api.External.MediaServers.Plex.Models;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Hubs;
using Ombi.Schedule.Jobs.Ombi;
using Ombi.Schedule.Jobs.Plex.Interfaces;
using Ombi.Store.Entities;
using Ombi.Store.Repository;
using Quartz;

namespace Ombi.Schedule.Jobs.Plex
{
    public class PlexEpisodeSync : IPlexEpisodeSync
    {
        public PlexEpisodeSync(ISettingsService<PlexSettings> s, ILogger<PlexEpisodeSync> log, IPlexApi plexApi,
            IPlexContentRepository repo, INotificationHubService notificationHubService)
        {
            _settings = s;
            _log = log;
            _api = plexApi;
            _repo = repo;
            _notification = notificationHubService;
            _settings.ClearCache();
        }

        private readonly ISettingsService<PlexSettings> _settings;
        private readonly ILogger<PlexEpisodeSync> _log;
        private readonly IPlexApi _api;
        private readonly IPlexContentRepository _repo;
        private readonly INotificationHubService _notification;

        public async Task Execute(IJobExecutionContext job)
        {
            try
            {
                var s = await _settings.GetSettingsAsync();
                if (!s.Enable)
                {
                    return;
                }

                await _notification.SendNotificationToAdmins("Plex Episode Sync Started");

                var servers = s.Servers ?? new List<PlexServers>();
                var observedEpisodeKeys = new HashSet<string>();
                var fullSnapshotComplete = servers.Count > 0;

                foreach (var server in servers)
                {
                    try
                    {
                        observedEpisodeKeys.UnionWith(await Cache(server));
                    }
                    catch (Exception e)
                    {
                        fullSnapshotComplete = false;
                        _log.LogWarning(LoggingEvents.PlexEpisodeCacher, e,
                            "Plex episode snapshot failed for server {ServerName}; stale episode cache entries will be preserved.",
                            server.Name);
                    }
                }

                if (fullSnapshotComplete)
                {
                    await ReconcileEpisodeCache(observedEpisodeKeys);
                }
                else
                {
                    _log.LogWarning("Plex episode snapshot was incomplete; stale Plex episode cache entries were preserved.");
                }
            }
            catch (Exception e)
            {
                await _notification.SendNotificationToAdmins("Plex Episode Sync Failed");
                _log.LogError(LoggingEvents.Cacher, e, "Caching Episodes Failed");
            }

            _log.LogInformation("Plex Episode Sync Finished - Triggering Metadata refresh");
            await OmbiQuartz.TriggerJob(nameof(IRefreshMetadata), "System");

            await _notification.SendNotificationToAdmins("Plex Episode Sync Finished");
        }

        private async Task<HashSet<string>> Cache(PlexServers settings)
        {
            if (!Validate(settings))
            {
                throw new InvalidOperationException($"Plex episode sync validation failed for server '{settings?.Name}'.");
            }

            var sections = await _api.GetLibrarySections(settings.PlexAuthToken, settings.FullUri);
            if (sections?.MediaContainer == null)
            {
                throw new InvalidOperationException($"Plex returned no library-section snapshot for server '{settings.Name}'.");
            }

            var observedEpisodeKeys = new HashSet<string>();
            var tvSections = sections.MediaContainer.Directory
                ?.Where(x => string.Equals(x.type, PlexMediaType.Show.ToString(), StringComparison.CurrentCultureIgnoreCase))
                ?? Enumerable.Empty<Directory>();

            foreach (var section in tvSections)
            {
                if (settings.PlexSelectedLibraries.Any())
                {
                    if (settings.PlexSelectedLibraries.Any(x => x.Enabled))
                    {
                        var keys = settings.PlexSelectedLibraries.Where(x => x.Enabled).Select(x => x.Key.ToString())
                            .ToList();
                        if (!keys.Contains(section.key))
                        {
                            continue;
                        }
                    }
                }

                observedEpisodeKeys.UnionWith(await GetEpisodes(settings, section));
            }

            return observedEpisodeKeys;
        }

        private async Task<HashSet<string>> GetEpisodes(PlexServers settings, Directory section)
        {
            var observedEpisodeKeys = new HashSet<string>();
            var currentPosition = 0;
            var resultCount = settings.EpisodeBatchSize == 0 ? 150 : settings.EpisodeBatchSize;
            var currentEpisodes = _repo.GetAllEpisodes().Cast<PlexEpisode>();
            var episodes = await _api.GetAllEpisodes(settings.PlexAuthToken, settings.FullUri, section.key, currentPosition, resultCount);
            if (episodes?.MediaContainer == null)
            {
                throw new InvalidOperationException(
                    $"Plex returned no episode snapshot for server '{settings.Name}', library '{section.key}'.");
            }

            _log.LogInformation(LoggingEvents.PlexEpisodeCacher,
                $"Total Epsiodes found for {episodes.MediaContainer.librarySectionTitle} = {episodes.MediaContainer.totalSize}");

            AddObservedEpisodeKeys(observedEpisodeKeys, episodes.MediaContainer.Metadata);
            await ProcessEpsiodes(episodes.MediaContainer.Metadata ?? Array.Empty<Metadata>(), currentEpisodes);
            currentPosition += resultCount;

            while (currentPosition < episodes.MediaContainer.totalSize)
            {
                var ep = await _api.GetAllEpisodes(settings.PlexAuthToken, settings.FullUri, section.key, currentPosition,
                    resultCount);
                if (ep?.MediaContainer == null)
                {
                    throw new InvalidOperationException(
                        $"Plex returned an incomplete episode page for server '{settings.Name}', library '{section.key}', offset {currentPosition}.");
                }

                AddObservedEpisodeKeys(observedEpisodeKeys, ep.MediaContainer.Metadata);
                await ProcessEpsiodes(ep.MediaContainer.Metadata ?? Array.Empty<Metadata>(), currentEpisodes);
                _log.LogInformation(LoggingEvents.PlexEpisodeCacher,
                    $"Processed {resultCount} more episodes. Total Remaining {episodes.MediaContainer.totalSize - currentPosition}");
                currentPosition += resultCount;
            }

            _log.LogInformation(LoggingEvents.PlexEpisodeCacher, "We have finished caching the episodes.");
            await _repo.SaveChangesAsync();
            return observedEpisodeKeys;
        }

        private async Task ReconcileEpisodeCache(HashSet<string> observedEpisodeKeys)
        {
            var cachedEpisodes = await _repo.GetAllEpisodes().Cast<PlexEpisode>().ToListAsync();
            var staleEpisodes = cachedEpisodes
                .Where(x => string.IsNullOrWhiteSpace(x.Key) || !observedEpisodeKeys.Contains(x.Key))
                .ToList();

            if (staleEpisodes.Count > 0)
            {
                await _repo.DeleteEpisodeRange(staleEpisodes);
            }

            _log.LogInformation(
                "Plex episode reconciliation completed. Observed={ObservedCount}, Removed={RemovedCount}",
                observedEpisodeKeys.Count,
                staleEpisodes.Count);
        }

        private static void AddObservedEpisodeKeys(HashSet<string> observedEpisodeKeys, IEnumerable<Metadata> episodes)
        {
            foreach (var episode in episodes ?? Enumerable.Empty<Metadata>())
            {
                if (string.IsNullOrWhiteSpace(episode.ratingKey))
                {
                    continue;
                }

                observedEpisodeKeys.Add(episode.ratingKey);
            }
        }


        public async Task<HashSet<PlexEpisode>> ProcessEpsiodes(Metadata[] episodes, IQueryable<PlexEpisode> currentEpisodes)
        {
            var ep = new HashSet<PlexEpisode>();
            try
            {
                foreach (var episode in episodes)
                {
                    // I don't think we need to get the metadata, we only need to get the metadata if we need the provider id (TheTvDbid). Why do we need it for episodes?
                    // We have the parent and grandparent rating keys to link up to the season and series
                    //var metadata = _api.GetEpisodeMetaData(server.PlexAuthToken, server.FullUri, episode.ratingKey);

                    // This does seem to work, it looks like we can somehow get different rating, grandparent and parent keys with episodes. Not sure how.
                    var epExists = currentEpisodes.Any(x => episode.ratingKey == x.Key &&
                                                              episode.grandparentRatingKey == x.GrandparentKey);
                    if (epExists)
                    {
                        continue;
                    }

                    // Let's check if we have the parent
                    var seriesExists = await _repo.GetByKey(episode.grandparentRatingKey);
                    if (seriesExists == null)
                    {
                        // Ok let's try and match it to a title. TODO (This is experimental)
                        seriesExists = await _repo.GetAll().FirstOrDefaultAsync(x =>
                            x.Title == episode.grandparentTitle);
                        if (seriesExists == null)
                        {
                            _log.LogWarning(
                                "The episode title {0} we cannot find the parent series. The episode grandparentKey = {1}, grandparentTitle = {2}",
                                episode.title, episode.grandparentRatingKey, episode.grandparentTitle);
                            continue;
                        }

                        // Set the rating key to the correct one
                        episode.grandparentRatingKey = seriesExists.Key;
                    }

                    // Sanity checks
                    if (episode.index == 0)
                    {
                        _log.LogWarning($"Episode {episode.title} has no episode number. Skipping.");
                        continue;
                    }

                    ep.Add(new PlexEpisode
                    {
                        EpisodeNumber = episode.index,
                        SeasonNumber = episode.parentIndex,
                        GrandparentKey = episode.grandparentRatingKey,
                        ParentKey = episode.parentRatingKey,
                        Key = episode.ratingKey,
                        Title = episode.title
                    });
                }

                await _repo.AddRange(ep);
                return ep;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private bool Validate(PlexServers settings)
        {
            if (string.IsNullOrEmpty(settings.PlexAuthToken))
            {
                return false;
            }

            return true;
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                //_settings?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
