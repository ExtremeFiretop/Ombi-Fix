using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Api.External.ExternalApis.Sonarr.Models;
using Ombi.Api.External.ExternalApis.TheMovieDb;
using Ombi.Api.External.ExternalApis.TheMovieDb.Models;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Schedule.Jobs.Radarr;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Context;
using Ombi.Store.Entities;
using Ombi.Store.Repository.Requests;
using Quartz;

namespace Ombi.Schedule.Jobs.Sonarr
{
    public class SonarrSync : ISonarrSync
    {
        public SonarrSync(ISettingsService<SonarrSettings> s, ISonarrV3Api api, ILogger<SonarrSync> l, ExternalContext ctx,
            IMovieDbApi movieDbApi, ITvRequestRepository tvRequestRepository)
        {
            _settings = s;
            _api = api;
            _log = l;
            _ctx = ctx;
            _movieDbApi = movieDbApi;
            _tvRequestRepository = tvRequestRepository;
            _settings.ClearCache();
        }

        private readonly ISettingsService<SonarrSettings> _settings;
        private readonly ISonarrV3Api _api;
        private readonly ILogger<SonarrSync> _log;
        private readonly ExternalContext _ctx;
        private readonly IMovieDbApi _movieDbApi;
        private readonly ITvRequestRepository _tvRequestRepository;

        public async Task Execute(IJobExecutionContext job)
        {
            try
            {
                try
                {
                    var removedRequestRows = await _tvRequestRepository.CleanupOrphanedRequestData();
                    if (removedRequestRows > 0)
                    {
                        _log.LogInformation(
                            "Media request maintenance removed {RemovedRows} orphaned or empty TV request row(s).",
                            removedRequestRows);
                    }
                }
                catch (Exception maintenanceException)
                {
                    // Request-table housekeeping must never prevent the Sonarr cache from syncing.
                    _log.LogWarning(maintenanceException,
                        "Could not run TV request maintenance before the Sonarr sync; continuing with the Sonarr sync.");
                }

                var settings = await _settings.GetSettingsAsync();
                if (!settings.Enabled)
                {
                    return;
                }

                var series = await _api.GetSeries(settings.ApiKey, settings.FullUri);
                if (series == null)
                {
                    _log.LogWarning("Sonarr returned no series snapshot; preserving the existing Sonarr cache.");
                    return;
                }

                var sonarrSeries = series as ImmutableHashSet<SonarrSeries> ?? series.ToImmutableHashSet();
                var ids = sonarrSeries.Select(x => new SonarrDto
                {
                    TvDbId = x.tvdbId,
                    ImdbId = x.imdbId,
                    Title = x.title,
                    MovieDbId = 0,
                    Id = x.id,
                    Monitored = x.monitored,
                    EpisodeFileCount = x.episodeFileCount
                }).ToHashSet();

                // Build the complete replacement snapshot in memory before deleting anything from
                // OmbiExternal. If any Sonarr/TMDb call fails, the outer catch preserves the last
                // known-good cache rather than leaving a partially rebuilt cache behind.
                var seriesSnapshot = new List<SonarrCache>();
                var episodeSnapshot = new List<SonarrEpisodeCache>();

                foreach (var id in ids)
                {
                    var cache = new SonarrCache
                    {
                        TvDbId = id.TvDbId
                    };

                    var findResult = await _movieDbApi.Find(id.TvDbId.ToString(), ExternalSource.tvdb_id);
                    if (findResult?.tv_results?.Any() == true)
                    {
                        cache.TheMovieDbId = findResult.tv_results.FirstOrDefault()?.id ?? -1;
                        id.MovieDbId = cache.TheMovieDbId;
                    }

                    seriesSnapshot.Add(cache);
                }

                foreach (var s in ids)
                {
                    if (!s.Monitored && s.EpisodeFileCount == 0)
                    {
                        // There cannot be a currently monitored/downloaded episode for this series,
                        // so its episode snapshot is intentionally empty.
                        continue;
                    }

                    _log.LogDebug("Syncing series: {Title}", s.Title);
                    var episodes = await _api.GetEpisodes(s.Id, settings.ApiKey, settings.FullUri);
                    if (episodes == null)
                    {
                        throw new InvalidOperationException($"Sonarr returned no episode snapshot for series '{s.Title}' ({s.Id}).");
                    }

                    episodeSnapshot.AddRange(episodes
                        .Where(x => x.monitored || x.hasFile)
                        .Select(episode => new SonarrEpisodeCache
                        {
                            EpisodeNumber = episode.episodeNumber,
                            SeasonNumber = episode.seasonNumber,
                            TvDbId = s.TvDbId,
                            MovieDbId = s.MovieDbId,
                            HasFile = episode.hasFile
                        }));
                }

                var strat = _ctx.Database.CreateExecutionStrategy();
                await strat.ExecuteAsync(async () =>
                {
                    using var tran = await _ctx.Database.BeginTransactionAsync();

                    // Reconcile both tables from one complete snapshot. This removes episode rows for
                    // series that were deleted from Sonarr as well as rows for episodes that are no
                    // longer monitored and no longer have files.
                    await _ctx.Database.ExecuteSqlRawAsync("DELETE FROM SonarrEpisodeCache");
                    await _ctx.Database.ExecuteSqlRawAsync("DELETE FROM SonarrCache");
                    await _ctx.Database.ResetAutoIncrementAsync("SonarrEpisodeCache");
                    await _ctx.Database.ResetAutoIncrementAsync("SonarrCache");

                    await _ctx.SonarrCache.AddRangeAsync(seriesSnapshot);
                    await _ctx.SonarrEpisodeCache.AddRangeAsync(episodeSnapshot);
                    await _ctx.SaveChangesAsync();
                    await tran.CommitAsync();
                });

                _log.LogInformation(
                    "Reconciled Sonarr cache from a complete snapshot. Series={SeriesCount}, Episodes={EpisodeCount}",
                    seriesSnapshot.Count,
                    episodeSnapshot.Count);

                await OmbiQuartz.TriggerJob(nameof(IArrAvailabilityChecker), "DVR");
            }
            catch (Exception e)
            {
                _log.LogError(LoggingEvents.SonarrCacher, e,
                    "Exception when trying to cache Sonarr. Existing Sonarr cache was preserved unless the replacement transaction completed.");
            }
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                //_settings?.Dispose();
                _ctx?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private class SonarrDto
        {
            public int TvDbId { get; set; }
            public string ImdbId { get; set; }
            public string Title { get; set; }
            public int MovieDbId { get; set; }
            public int Id { get; set; }
            public bool Monitored { get; set; }
            public int EpisodeFileCount { get; set; }
        }
    }
}