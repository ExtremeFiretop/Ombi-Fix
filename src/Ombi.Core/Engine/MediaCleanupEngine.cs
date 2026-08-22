using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Core.Authentication;
using Ombi.Core.Engine.Interfaces;
using Ombi.Core.Helpers;
using Ombi.Core.Models.MediaCleanup;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Settings.Settings.Models;
using Ombi.Settings.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Repository.Requests;

namespace Ombi.Core.Engine
{
    public class MediaCleanupEngine : IMediaCleanupEngine
    {
        private static readonly SemaphoreSlim StateLock = new SemaphoreSlim(1, 1);

        private readonly ISettingsService<MediaCleanupSettings> _settings;
        private readonly ISettingsService<MediaCleanupState> _state;
        private readonly ISettingsService<RadarrSettings> _radarrSettings;
        private readonly ISettingsService<Radarr4KSettings> _radarr4KSettings;
        private readonly ISettingsService<SonarrSettings> _sonarrSettings;
        private readonly IMovieRequestRepository _movieRequests;
        private readonly ITvRequestRepository _tvRequests;
        private readonly ICurrentUser _currentUser;
        private readonly OmbiUserManager _userManager;
        private readonly IRadarrV3Api _radarr;
        private readonly ISonarrV3Api _sonarr;
        private readonly IMediaCacheService _mediaCache;
        private readonly ILogger<MediaCleanupEngine> _logger;

        public MediaCleanupEngine(
            ISettingsService<MediaCleanupSettings> settings,
            ISettingsService<MediaCleanupState> state,
            ISettingsService<RadarrSettings> radarrSettings,
            ISettingsService<Radarr4KSettings> radarr4KSettings,
            ISettingsService<SonarrSettings> sonarrSettings,
            IMovieRequestRepository movieRequests,
            ITvRequestRepository tvRequests,
            ICurrentUser currentUser,
            OmbiUserManager userManager,
            IRadarrV3Api radarr,
            ISonarrV3Api sonarr,
            IMediaCacheService mediaCache,
            ILogger<MediaCleanupEngine> logger)
        {
            _settings = settings;
            _state = state;
            _radarrSettings = radarrSettings;
            _radarr4KSettings = radarr4KSettings;
            _sonarrSettings = sonarrSettings;
            _movieRequests = movieRequests;
            _tvRequests = tvRequests;
            _currentUser = currentUser;
            _userManager = userManager;
            _radarr = radarr;
            _sonarr = sonarr;
            _mediaCache = mediaCache;
            _logger = logger;
        }

        public async Task<MediaCleanupOverview> GetOverview()
        {
            var settings = await _settings.GetSettingsAsync();
            var user = await _currentUser.GetUser();
            if (user == null)
            {
                return new MediaCleanupOverview { Settings = settings };
            }

            var permissions = await GetPermissions(user);
            var state = await LoadState();
            var active = state.Requests
                .Where(IsActive)
                .GroupBy(x => new { x.RequestType, x.MediaRequestId })
                .ToDictionary(x => (x.Key.RequestType, x.Key.MediaRequestId), x => x.OrderByDescending(r => r.CreatedAt).First());

            var result = new MediaCleanupOverview
            {
                Settings = settings,
                CanRequestRemoval = permissions.CanRequestRemoval,
                CanDeleteOwnMedia = permissions.CanDeleteOwnMedia,
                CanVote = permissions.CanVote,
                CanManage = permissions.CanManage
            };

            var movieSizes = await GetMovieSizes();
            var tvSizes = await GetTvSizes();
            var now = DateTime.UtcNow;

            var movies = await _movieRequests.GetWithUser()
                .Where(x => x.Available)
                .OrderBy(x => x.Title)
                .ToListAsync();

            foreach (var movie in movies)
            {
                active.TryGetValue((RequestType.Movie, movie.Id), out var cleanup);
                var availableSince = movie.MarkedAsAvailable ?? (movie.RequestedDate == default ? (DateTime?)null : movie.RequestedDate);
                var owned = movie.RequestedUserId == user.Id;
                var ageEligible = IsAgeEligible(availableSince, settings.MinimumMediaAgeDays, now);
                if (!CanSeeItem(cleanup, owned, settings, permissions, user.Id))
                {
                    continue;
                }

                result.Items.Add(new MediaCleanupItemViewModel
                {
                    RequestType = RequestType.Movie,
                    RequestId = movie.Id,
                    Title = movie.Title,
                    PosterPath = movie.PosterPath,
                    RequestedBy = movie.RequestedUser?.UserAlias ?? movie.RequestedByAlias,
                    OwnedByCurrentUser = owned,
                    CanRequestOwnRemoval = cleanup == null && owned && CanUseOwnRemoval(settings, permissions),
                    CanNominate = cleanup == null && ageEligible && settings.CommunityCleanup != CommunityCleanupMode.Off && permissions.CanVote,
                    CanVote = cleanup != null && cleanup.Origin == MediaCleanupOrigin.Community && settings.CommunityCleanup != CommunityCleanupMode.Off && permissions.CanVote && IsVoteable(cleanup),
                    CanManage = cleanup != null && permissions.CanManage,
                    CanCancel = cleanup != null && (permissions.CanManage || cleanup.RequestedByUserId == user.Id),
                    CommunityAgeEligible = ageEligible,
                    AvailableSince = availableSince,
                    SizeOnDisk = movieSizes.TryGetValue(movie.TheMovieDbId, out var movieSize) ? movieSize : 0,
                    Cleanup = ToViewModel(cleanup, user.Id, settings)
                });
            }

            var tvRequests = await _tvRequests.GetLite()
                .Where(x => x.ChildRequests.Any() && x.ChildRequests.All(c => c.Available))
                .OrderBy(x => x.Title)
                .ToListAsync();

            foreach (var tv in tvRequests)
            {
                active.TryGetValue((RequestType.TvShow, tv.Id), out var cleanup);
                var owners = tv.ChildRequests.Select(x => x.RequestedUserId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                var owned = owners.Count == 1 && owners[0] == user.Id;
                var availableSince = tv.ChildRequests
                    .Select(x => x.MarkedAsAvailable ?? (x.RequestedDate == default ? (DateTime?)null : x.RequestedDate))
                    .Where(x => x.HasValue)
                    .OrderByDescending(x => x.Value)
                    .FirstOrDefault();
                var ageEligible = IsAgeEligible(availableSince, settings.MinimumMediaAgeDays, now);
                var requestedBy = tv.ChildRequests.Select(x => x.RequestedUser?.UserAlias ?? x.RequestedByAlias)
                    .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                if (!CanSeeItem(cleanup, owned, settings, permissions, user.Id))
                {
                    continue;
                }

                result.Items.Add(new MediaCleanupItemViewModel
                {
                    RequestType = RequestType.TvShow,
                    RequestId = tv.Id,
                    Title = tv.Title,
                    PosterPath = tv.PosterPath,
                    RequestedBy = requestedBy.Count == 1 ? requestedBy[0] : requestedBy.Count > 1 ? "Multiple users" : string.Empty,
                    OwnedByCurrentUser = owned,
                    CanRequestOwnRemoval = cleanup == null && owned && CanUseOwnRemoval(settings, permissions),
                    CanNominate = cleanup == null && ageEligible && settings.CommunityCleanup != CommunityCleanupMode.Off && permissions.CanVote,
                    CanVote = cleanup != null && cleanup.Origin == MediaCleanupOrigin.Community && settings.CommunityCleanup != CommunityCleanupMode.Off && permissions.CanVote && IsVoteable(cleanup),
                    CanManage = cleanup != null && permissions.CanManage,
                    CanCancel = cleanup != null && (permissions.CanManage || cleanup.RequestedByUserId == user.Id),
                    CommunityAgeEligible = ageEligible,
                    AvailableSince = availableSince,
                    SizeOnDisk = tvSizes.TryGetValue(tv.TvDbId, out var tvSize) ? tvSize : 0,
                    Cleanup = ToViewModel(cleanup, user.Id, settings)
                });
            }

            return result;
        }

        public async Task<MediaCleanupActionResult> RequestOwnRemoval(RequestType requestType, int requestId)
        {
            await StateLock.WaitAsync();
            try
            {
                var settings = await _settings.GetSettingsAsync();
                if (settings.OwnRequestRemoval == OwnRequestRemovalMode.Off)
                {
                    return Fail("Removing your own media is disabled by the administrator.");
                }

                var user = await _currentUser.GetUser();
                if (user == null)
                {
                    return Fail("User could not be resolved.");
                }

                var permissions = await GetPermissions(user);
                if (!permissions.CanRequestRemoval)
                {
                    return Fail("You do not have permission to request media removal.");
                }

                if (settings.OwnRequestRemoval == OwnRequestRemovalMode.ImmediateDeletion && !permissions.CanDeleteOwnMedia)
                {
                    return Fail("Immediate deletion requires the DeleteOwnMedia role.");
                }

                var target = await ResolveTarget(requestType, requestId);
                if (target == null || !target.Available)
                {
                    return Fail("The available Ombi request could not be found.");
                }

                if (target.OwnerUserIds.Count != 1 || target.OwnerUserIds[0] != user.Id)
                {
                    return Fail(requestType == RequestType.TvShow
                        ? "A TV series can only be removed as your own request when all Ombi requests for the series belong to you. Use community cleanup when multiple users requested it."
                        : "You can only remove media that you requested.");
                }

                var state = await LoadState();
                if (FindActive(state, requestType, requestId) != null)
                {
                    return Fail("This title already has an active cleanup request.");
                }

                var record = CreateRecord(target, user.Id, MediaCleanupOrigin.OwnRequest);
                if (settings.OwnRequestRemoval == OwnRequestRemovalMode.RequestRemoval)
                {
                    record.Status = MediaCleanupStatus.PendingAdminApproval;
                    state.Requests.Add(record);
                    await SaveState(state);
                    return Success("Removal request submitted for administrator approval.", record.Id);
                }

                record.Status = MediaCleanupStatus.ScheduledForDeletion;
                record.ScheduledForDeletionAt = DateTime.UtcNow;
                state.Requests.Add(record);
                await SaveState(state);

                await ExecuteDeletion(record, settings);
                await SaveState(state);
                return record.Status == MediaCleanupStatus.Completed
                    ? Success("Media was removed successfully.", record.Id)
                    : Fail(record.FailureReason ?? "Media deletion failed.", record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        public async Task<MediaCleanupActionResult> Nominate(RequestType requestType, int requestId)
        {
            await StateLock.WaitAsync();
            try
            {
                var settings = await _settings.GetSettingsAsync();
                if (settings.CommunityCleanup == CommunityCleanupMode.Off)
                {
                    return Fail("Community cleanup voting is disabled.");
                }

                var user = await _currentUser.GetUser();
                if (user == null)
                {
                    return Fail("User could not be resolved.");
                }

                var permissions = await GetPermissions(user);
                if (!permissions.CanVote)
                {
                    return Fail("You do not have permission to participate in media cleanup voting.");
                }

                var target = await ResolveTarget(requestType, requestId);
                if (target == null || !target.Available)
                {
                    return Fail("The available Ombi request could not be found.");
                }

                if (!IsAgeEligible(target.AvailableSince, settings.MinimumMediaAgeDays, DateTime.UtcNow))
                {
                    return Fail($"This title must be available for at least {settings.MinimumMediaAgeDays} days before community cleanup can be started.");
                }

                var state = await LoadState();
                var existing = FindActive(state, requestType, requestId);
                if (existing != null)
                {
                    return Fail("This title already has an active cleanup vote.", existing.Id);
                }

                var record = CreateRecord(target, user.Id, MediaCleanupOrigin.Community);
                record.Status = MediaCleanupStatus.Voting;
                record.VotingEndsAt = DateTime.UtcNow.AddDays(Math.Max(1, settings.VotingPeriodDays));
                record.Votes.Add(new MediaCleanupVoteRecord
                {
                    UserId = user.Id,
                    Vote = MediaCleanupVoteType.Delete,
                    Date = DateTime.UtcNow
                });
                EvaluateCommunity(record, settings, DateTime.UtcNow);
                state.Requests.Add(record);
                await SaveState(state);

                return Success("Cleanup vote started. Your delete vote was recorded.", record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        public async Task<MediaCleanupActionResult> Vote(string cleanupRequestId, MediaCleanupVoteType vote)
        {
            await StateLock.WaitAsync();
            try
            {
                var settings = await _settings.GetSettingsAsync();
                if (settings.CommunityCleanup == CommunityCleanupMode.Off)
                {
                    return Fail("Community cleanup voting is disabled.");
                }

                var user = await _currentUser.GetUser();
                if (user == null)
                {
                    return Fail("User could not be resolved.");
                }

                var permissions = await GetPermissions(user);
                if (!permissions.CanVote)
                {
                    return Fail("You do not have permission to vote on media cleanup.");
                }

                var state = await LoadState();
                var record = state.Requests.FirstOrDefault(x => x.Id == cleanupRequestId);
                if (record == null || record.Origin != MediaCleanupOrigin.Community)
                {
                    return Fail("This cleanup request is not open for voting.");
                }

                EvaluateCommunity(record, settings, DateTime.UtcNow);
                if (!IsVoteable(record))
                {
                    await SaveState(state);
                    return Fail("This cleanup request is no longer open for voting.", record.Id);
                }

                var existing = record.Votes.FirstOrDefault(x => x.UserId == user.Id);
                if (existing == null)
                {
                    record.Votes.Add(new MediaCleanupVoteRecord { UserId = user.Id, Vote = vote, Date = DateTime.UtcNow });
                }
                else
                {
                    existing.Vote = vote;
                    existing.Date = DateTime.UtcNow;
                }

                EvaluateCommunity(record, settings, DateTime.UtcNow);
                await SaveState(state);
                return Success(vote == MediaCleanupVoteType.Delete ? "Delete vote recorded." : "Keep vote recorded.", record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        public async Task<MediaCleanupActionResult> Approve(string cleanupRequestId)
        {
            await StateLock.WaitAsync();
            try
            {
                var user = await _currentUser.GetUser();
                if (user == null || !(await GetPermissions(user)).CanManage)
                {
                    return Fail("You do not have permission to manage media cleanup.");
                }

                var settings = await _settings.GetSettingsAsync();
                var state = await LoadState();
                var record = state.Requests.FirstOrDefault(x => x.Id == cleanupRequestId);
                if (record == null || record.Status != MediaCleanupStatus.PendingAdminApproval)
                {
                    return Fail("This cleanup request is not awaiting administrator approval.");
                }

                if (!IsOriginEnabled(record, settings))
                {
                    return Fail("This cleanup system is currently disabled in Media Cleanup settings.");
                }

                record.ApprovedByUserId = user.Id;
                record.Status = MediaCleanupStatus.ScheduledForDeletion;
                record.ScheduledForDeletionAt = DateTime.UtcNow.AddDays(Math.Max(0, settings.GracePeriodDays));
                await SaveState(state);
                return Success("Cleanup approved and scheduled for deletion.", record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        public async Task<MediaCleanupActionResult> Reject(string cleanupRequestId)
        {
            return await SetTerminalState(cleanupRequestId, MediaCleanupStatus.Rejected, "Cleanup request rejected.", true);
        }

        public async Task<MediaCleanupActionResult> Cancel(string cleanupRequestId)
        {
            await StateLock.WaitAsync();
            try
            {
                var user = await _currentUser.GetUser();
                if (user == null)
                {
                    return Fail("User could not be resolved.");
                }

                var state = await LoadState();
                var record = state.Requests.FirstOrDefault(x => x.Id == cleanupRequestId);
                if (record == null || !IsActive(record))
                {
                    return Fail("This cleanup request is no longer active.");
                }

                var canManage = (await GetPermissions(user)).CanManage;
                if (!canManage && record.RequestedByUserId != user.Id)
                {
                    return Fail("Only the user who started the cleanup request or a cleanup manager can cancel it.");
                }

                record.Status = MediaCleanupStatus.Cancelled;
                record.ScheduledForDeletionAt = null;
                await SaveState(state);
                return Success("Cleanup request cancelled.", record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        public async Task ProcessPending()
        {
            await StateLock.WaitAsync();
            try
            {
                var settings = await _settings.GetSettingsAsync();
                var state = await LoadState();
                var now = DateTime.UtcNow;
                var changed = false;

                foreach (var record in state.Requests.Where(IsActive).ToList())
                {
                    if (record.Origin == MediaCleanupOrigin.Community)
                    {
                        var before = record.Status;
                        var beforeScheduled = record.ScheduledForDeletionAt;
                        EvaluateCommunity(record, settings, now);
                        changed |= before != record.Status || beforeScheduled != record.ScheduledForDeletionAt;
                    }

                    if (record.Status == MediaCleanupStatus.ScheduledForDeletion &&
                        record.ScheduledForDeletionAt.HasValue &&
                        record.ScheduledForDeletionAt.Value <= now &&
                        IsOriginEnabled(record, settings))
                    {
                        await ExecuteDeletion(record, settings);
                        changed = true;
                    }
                }

                if (changed)
                {
                    await SaveState(state);
                }
            }
            finally
            {
                StateLock.Release();
            }
        }

        private async Task<MediaCleanupActionResult> SetTerminalState(string id, MediaCleanupStatus status, string message, bool managerOnly)
        {
            await StateLock.WaitAsync();
            try
            {
                var user = await _currentUser.GetUser();
                if (user == null)
                {
                    return Fail("User could not be resolved.");
                }

                if (managerOnly && !(await GetPermissions(user)).CanManage)
                {
                    return Fail("You do not have permission to manage media cleanup.");
                }

                var state = await LoadState();
                var record = state.Requests.FirstOrDefault(x => x.Id == id);
                if (record == null || !IsActive(record))
                {
                    return Fail("This cleanup request is no longer active.");
                }

                record.Status = status;
                record.ScheduledForDeletionAt = null;
                await SaveState(state);
                return Success(message, record.Id);
            }
            finally
            {
                StateLock.Release();
            }
        }

        private async Task ExecuteDeletion(MediaCleanupRecord record, MediaCleanupSettings settings)
        {
            try
            {
                var externalDeleted = record.RequestType == RequestType.Movie
                    ? await DeleteMovie(record, settings)
                    : record.RequestType == RequestType.TvShow && await DeleteTv(record, settings);

                if (!externalDeleted)
                {
                    throw new InvalidOperationException(record.RequestType == RequestType.Movie
                        ? "The movie could not be found in an enabled Radarr instance."
                        : "The series could not be found in the enabled Sonarr instance.");
                }

                if (record.RequestType == RequestType.Movie)
                {
                    var movieRequest = await _movieRequests.GetAll().FirstOrDefaultAsync(x => x.Id == record.MediaRequestId);
                    if (movieRequest != null)
                    {
                        await _movieRequests.Delete(movieRequest);
                    }
                }
                else
                {
                    var tvRequest = await _tvRequests.Get().FirstOrDefaultAsync(x => x.Id == record.MediaRequestId);
                    if (tvRequest != null)
                    {
                        await _tvRequests.Delete(tvRequest);
                    }
                }

                await _mediaCache.Purge();
                record.Status = MediaCleanupStatus.Completed;
                record.CompletedAt = DateTime.UtcNow;
                record.ScheduledForDeletionAt = null;
                record.FailureReason = null;
                _logger.LogInformation("Media cleanup removed {RequestType} '{Title}' ({CleanupId})", record.RequestType, record.Title, record.Id);
            }
            catch (Exception ex)
            {
                record.Status = MediaCleanupStatus.Failed;
                record.FailureReason = ex.Message;
                record.ScheduledForDeletionAt = null;
                _logger.LogError(ex, "Media cleanup failed for {RequestType} '{Title}' ({CleanupId})", record.RequestType, record.Title, record.Id);
            }
        }

        private async Task<bool> DeleteMovie(MediaCleanupRecord record, MediaCleanupSettings cleanupSettings)
        {
            var deleted = false;
            var deletedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var regular = await _radarrSettings.GetSettingsAsync();
            if (regular.Enabled)
            {
                deleted |= await DeleteMovieFromRadarr(record.TheMovieDbId, regular, cleanupSettings, deletedKeys);
            }

            var fourK = await _radarr4KSettings.GetSettingsAsync();
            if (fourK.Enabled)
            {
                deleted |= await DeleteMovieFromRadarr(record.TheMovieDbId, fourK, cleanupSettings, deletedKeys);
            }

            return deleted;
        }

        private async Task<bool> DeleteMovieFromRadarr(int tmdbId, RadarrSettings radarrSettings, MediaCleanupSettings cleanupSettings, HashSet<string> deletedKeys)
        {
            var movies = await _radarr.GetMovies(radarrSettings.ApiKey, radarrSettings.FullUri);
            var matches = movies.Where(x => x.tmdbId == tmdbId).ToList();
            var deleted = false;
            foreach (var movie in matches)
            {
                var key = $"{radarrSettings.FullUri}|{movie.id}";
                if (!deletedKeys.Add(key))
                {
                    continue;
                }
                await _radarr.DeleteMovie(movie.id, radarrSettings.ApiKey, radarrSettings.FullUri, cleanupSettings.DeleteFiles, cleanupSettings.AddImportExclusion);
                deleted = true;
            }
            return deleted;
        }

        private async Task<bool> DeleteTv(MediaCleanupRecord record, MediaCleanupSettings cleanupSettings)
        {
            var sonarrSettings = await _sonarrSettings.GetSettingsAsync();
            if (!sonarrSettings.Enabled)
            {
                return false;
            }

            var series = await _sonarr.GetSeries(sonarrSettings.ApiKey, sonarrSettings.FullUri);
            var match = series.FirstOrDefault(x => x.tvdbId == record.TvDbId);
            if (match == null)
            {
                return false;
            }

            await _sonarr.DeleteSeries(match.id, sonarrSettings.ApiKey, sonarrSettings.FullUri, cleanupSettings.DeleteFiles, cleanupSettings.AddImportExclusion);
            return true;
        }

        private async Task<Dictionary<int, long>> GetMovieSizes()
        {
            var result = new Dictionary<int, long>();
            await AddRadarrSizes(await _radarrSettings.GetSettingsAsync(), result);
            await AddRadarrSizes(await _radarr4KSettings.GetSettingsAsync(), result);
            return result;
        }

        private async Task AddRadarrSizes(RadarrSettings settings, Dictionary<int, long> result)
        {
            if (!settings.Enabled)
            {
                return;
            }

            try
            {
                foreach (var movie in await _radarr.GetMovies(settings.ApiKey, settings.FullUri))
                {
                    result[movie.tmdbId] = (result.TryGetValue(movie.tmdbId, out var current) ? current : 0) + movie.sizeOnDisk;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Radarr sizes for the media cleanup page");
            }
        }

        private async Task<Dictionary<int, long>> GetTvSizes()
        {
            var result = new Dictionary<int, long>();
            var settings = await _sonarrSettings.GetSettingsAsync();
            if (!settings.Enabled)
            {
                return result;
            }

            try
            {
                foreach (var series in await _sonarr.GetSeries(settings.ApiKey, settings.FullUri))
                {
                    result[series.tvdbId] = series.sizeOnDisk;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Sonarr sizes for the media cleanup page");
            }

            return result;
        }

        private async Task<CleanupTarget> ResolveTarget(RequestType requestType, int requestId)
        {
            if (requestType == RequestType.Movie)
            {
                var movie = await _movieRequests.GetWithUser().FirstOrDefaultAsync(x => x.Id == requestId);
                if (movie == null)
                {
                    return null;
                }

                return new CleanupTarget
                {
                    RequestType = RequestType.Movie,
                    RequestId = movie.Id,
                    Title = movie.Title,
                    PosterPath = movie.PosterPath,
                    TheMovieDbId = movie.TheMovieDbId,
                    Available = movie.Available,
                    AvailableSince = movie.MarkedAsAvailable ?? (movie.RequestedDate == default ? (DateTime?)null : movie.RequestedDate),
                    OwnerUserIds = string.IsNullOrEmpty(movie.RequestedUserId) ? new List<string>() : new List<string> { movie.RequestedUserId }
                };
            }

            if (requestType == RequestType.TvShow)
            {
                var tv = await _tvRequests.GetLite().FirstOrDefaultAsync(x => x.Id == requestId);
                if (tv == null)
                {
                    return null;
                }

                return new CleanupTarget
                {
                    RequestType = RequestType.TvShow,
                    RequestId = tv.Id,
                    Title = tv.Title,
                    PosterPath = tv.PosterPath,
                    TheMovieDbId = tv.ExternalProviderId,
                    TvDbId = tv.TvDbId,
                    Available = tv.ChildRequests.Any() && tv.ChildRequests.All(x => x.Available),
                    AvailableSince = tv.ChildRequests
                        .Select(x => x.MarkedAsAvailable ?? (x.RequestedDate == default ? (DateTime?)null : x.RequestedDate))
                        .Where(x => x.HasValue)
                        .OrderByDescending(x => x.Value)
                        .FirstOrDefault(),
                    OwnerUserIds = tv.ChildRequests.Select(x => x.RequestedUserId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList()
                };
            }

            return null;
        }

        private MediaCleanupRecord CreateRecord(CleanupTarget target, string userId, MediaCleanupOrigin origin)
        {
            return new MediaCleanupRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                RequestType = target.RequestType,
                MediaRequestId = target.RequestId,
                Title = target.Title,
                PosterPath = target.PosterPath,
                TheMovieDbId = target.TheMovieDbId,
                TvDbId = target.TvDbId,
                AvailableSince = target.AvailableSince,
                RequestedByUserId = userId,
                OwnerUserIds = target.OwnerUserIds,
                Origin = origin,
                CreatedAt = DateTime.UtcNow
            };
        }

        private void EvaluateCommunity(MediaCleanupRecord record, MediaCleanupSettings settings, DateTime now)
        {
            if (record.Origin != MediaCleanupOrigin.Community || !IsActive(record))
            {
                return;
            }

            if (settings.CommunityCleanup == CommunityCleanupMode.Off)
            {
                return;
            }

            var keepVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Keep);
            var deleteVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Delete);
            var requesterVeto = settings.RequesterCanVeto && record.Votes.Any(x =>
                x.Vote == MediaCleanupVoteType.Keep && record.OwnerUserIds.Contains(x.UserId));
            var thresholdMet = !requesterVeto &&
                               deleteVotes >= Math.Max(1, settings.MinimumDeleteVotes) &&
                               deleteVotes - keepVotes >= Math.Max(0, settings.RequiredVoteMargin);

            if (settings.CommunityCleanup == CommunityCleanupMode.AutomaticAfterThreshold)
            {
                if (thresholdMet)
                {
                    if (record.Status != MediaCleanupStatus.ScheduledForDeletion)
                    {
                        record.Status = MediaCleanupStatus.ScheduledForDeletion;
                        record.ScheduledForDeletionAt = now.AddDays(Math.Max(0, settings.GracePeriodDays));
                    }
                }
                else if (record.Status == MediaCleanupStatus.ScheduledForDeletion)
                {
                    record.Status = MediaCleanupStatus.Voting;
                    record.ScheduledForDeletionAt = null;
                }
            }
            else if (settings.CommunityCleanup == CommunityCleanupMode.AdminApproval)
            {
                if (!string.IsNullOrEmpty(record.ApprovedByUserId))
                {
                    if (requesterVeto)
                    {
                        record.ApprovedByUserId = null;
                        record.Status = MediaCleanupStatus.Voting;
                        record.ScheduledForDeletionAt = null;
                    }
                    else
                    {
                        record.Status = MediaCleanupStatus.ScheduledForDeletion;
                    }
                }
                else
                {
                    record.Status = thresholdMet ? MediaCleanupStatus.PendingAdminApproval : MediaCleanupStatus.Voting;
                    record.ScheduledForDeletionAt = null;
                }
            }

            if (record.Status == MediaCleanupStatus.Voting && record.VotingEndsAt.HasValue && record.VotingEndsAt.Value <= now)
            {
                record.Status = MediaCleanupStatus.Rejected;
                record.ScheduledForDeletionAt = null;
            }
        }

        private MediaCleanupRequestViewModel ToViewModel(MediaCleanupRecord record, string userId, MediaCleanupSettings settings)
        {
            if (record == null)
            {
                return null;
            }

            return new MediaCleanupRequestViewModel
            {
                Id = record.Id,
                Origin = record.Origin,
                Status = record.Status,
                KeepVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Keep),
                DeleteVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Delete),
                RequesterVeto = settings.RequesterCanVeto && record.Votes.Any(x => x.Vote == MediaCleanupVoteType.Keep && record.OwnerUserIds.Contains(x.UserId)),
                MyVote = record.Votes.FirstOrDefault(x => x.UserId == userId)?.Vote,
                CreatedAt = record.CreatedAt,
                VotingEndsAt = record.VotingEndsAt,
                ScheduledForDeletionAt = record.ScheduledForDeletionAt,
                FailureReason = record.FailureReason
            };
        }

        private async Task<MediaCleanupState> LoadState()
        {
            _state.ClearCache();
            var state = await _state.GetSettingsAsync() ?? new MediaCleanupState();
            state.Requests ??= new List<MediaCleanupRecord>();
            foreach (var request in state.Requests)
            {
                request.OwnerUserIds ??= new List<string>();
                request.Votes ??= new List<MediaCleanupVoteRecord>();
            }
            return state;
        }

        private Task<bool> SaveState(MediaCleanupState state)
        {
            return _state.SaveSettingsAsync(state);
        }

        private async Task<CleanupPermissions> GetPermissions(OmbiUser user)
        {
            var admin = await _userManager.IsInRoleAsync(user, OmbiRoles.Admin);
            var powerUser = await _userManager.IsInRoleAsync(user, OmbiRoles.PowerUser);
            var privileged = admin || powerUser;
            return new CleanupPermissions
            {
                CanRequestRemoval = privileged || await _userManager.IsInRoleAsync(user, OmbiRoles.RequestMediaRemoval),
                CanDeleteOwnMedia = privileged || await _userManager.IsInRoleAsync(user, OmbiRoles.DeleteOwnMedia),
                CanVote = privileged || await _userManager.IsInRoleAsync(user, OmbiRoles.VoteOnMediaCleanup),
                CanManage = privileged || await _userManager.IsInRoleAsync(user, OmbiRoles.ManageMediaCleanup)
            };
        }

        private static bool CanUseOwnRemoval(MediaCleanupSettings settings, CleanupPermissions permissions)
        {
            if (settings.OwnRequestRemoval == OwnRequestRemovalMode.Off || !permissions.CanRequestRemoval)
            {
                return false;
            }
            return settings.OwnRequestRemoval != OwnRequestRemovalMode.ImmediateDeletion || permissions.CanDeleteOwnMedia;
        }

        private static bool CanSeeItem(MediaCleanupRecord cleanup, bool owned, MediaCleanupSettings settings, CleanupPermissions permissions, string userId)
        {
            if (cleanup != null && (permissions.CanManage || cleanup.RequestedByUserId == userId ||
                                    (cleanup.Origin == MediaCleanupOrigin.Community && permissions.CanVote)))
            {
                return true;
            }

            if (owned && CanUseOwnRemoval(settings, permissions))
            {
                return true;
            }

            return settings.CommunityCleanup != CommunityCleanupMode.Off && permissions.CanVote;
        }

        private static bool IsOriginEnabled(MediaCleanupRecord record, MediaCleanupSettings settings)
        {
            return record.Origin == MediaCleanupOrigin.OwnRequest
                ? settings.OwnRequestRemoval != OwnRequestRemovalMode.Off
                : settings.CommunityCleanup != CommunityCleanupMode.Off;
        }

        private static bool IsAgeEligible(DateTime? availableSince, int minimumDays, DateTime now)
        {
            if (minimumDays <= 0)
            {
                return true;
            }
            return availableSince.HasValue && availableSince.Value <= now.AddDays(-minimumDays);
        }

        private static bool IsActive(MediaCleanupRecord record)
        {
            return record.Status == MediaCleanupStatus.Voting ||
                   record.Status == MediaCleanupStatus.PendingAdminApproval ||
                   record.Status == MediaCleanupStatus.ScheduledForDeletion;
        }

        private static bool IsVoteable(MediaCleanupRecord record)
        {
            return record.Status == MediaCleanupStatus.Voting ||
                   record.Status == MediaCleanupStatus.PendingAdminApproval ||
                   record.Status == MediaCleanupStatus.ScheduledForDeletion;
        }

        private static MediaCleanupRecord FindActive(MediaCleanupState state, RequestType requestType, int requestId)
        {
            return state.Requests.LastOrDefault(x => x.RequestType == requestType && x.MediaRequestId == requestId && IsActive(x));
        }

        private static MediaCleanupActionResult Success(string message, string id = null)
        {
            return new MediaCleanupActionResult { Result = true, Message = message, CleanupRequestId = id };
        }

        private static MediaCleanupActionResult Fail(string message, string id = null)
        {
            return new MediaCleanupActionResult { Result = false, Message = message, CleanupRequestId = id };
        }

        private class CleanupTarget
        {
            public RequestType RequestType { get; set; }
            public int RequestId { get; set; }
            public string Title { get; set; }
            public string PosterPath { get; set; }
            public int TheMovieDbId { get; set; }
            public int TvDbId { get; set; }
            public bool Available { get; set; }
            public DateTime? AvailableSince { get; set; }
            public List<string> OwnerUserIds { get; set; } = new List<string>();
        }

        private class CleanupPermissions
        {
            public bool CanRequestRemoval { get; set; }
            public bool CanDeleteOwnMedia { get; set; }
            public bool CanVote { get; set; }
            public bool CanManage { get; set; }
        }
    }
}
