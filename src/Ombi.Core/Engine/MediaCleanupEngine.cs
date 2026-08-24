using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Api.External.MediaServers.Plex;
using Ombi.Core.Authentication;
using Ombi.Core.Engine.Interfaces;
using Ombi.Core.Helpers;
using Ombi.Core.Models.MediaCleanup;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Notifications;
using Ombi.Notifications.Models;
using Ombi.Settings.Settings.Models;
using Ombi.Settings.Settings.Models.External;
using Ombi.Settings.Settings.Models.Notifications;
using Ombi.Store.Entities;
using Ombi.Store.Repository;
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
        private readonly ISettingsService<PlexSettings> _plexSettings;
        private readonly IMovieRequestRepository _movieRequests;
        private readonly ITvRequestRepository _tvRequests;
        private readonly ICurrentUser _currentUser;
        private readonly OmbiUserManager _userManager;
        private readonly IRadarrV3Api _radarr;
        private readonly ISonarrV3Api _sonarr;
        private readonly IPlexApi _plex;
        private readonly IPlexContentRepository _plexContent;
        private readonly IExternalRepository<RadarrCache> _radarrCache;
        private readonly IExternalRepository<SonarrCache> _sonarrCache;
        private readonly IExternalRepository<SonarrEpisodeCache> _sonarrEpisodeCache;
        private readonly IMediaCacheService _mediaCache;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<MediaCleanupEngine> _logger;

        public MediaCleanupEngine(
            ISettingsService<MediaCleanupSettings> settings,
            ISettingsService<MediaCleanupState> state,
            ISettingsService<RadarrSettings> radarrSettings,
            ISettingsService<Radarr4KSettings> radarr4KSettings,
            ISettingsService<SonarrSettings> sonarrSettings,
            ISettingsService<PlexSettings> plexSettings,
            IMovieRequestRepository movieRequests,
            ITvRequestRepository tvRequests,
            ICurrentUser currentUser,
            OmbiUserManager userManager,
            IRadarrV3Api radarr,
            ISonarrV3Api sonarr,
            IPlexApi plex,
            IPlexContentRepository plexContent,
            IExternalRepository<RadarrCache> radarrCache,
            IExternalRepository<SonarrCache> sonarrCache,
            IExternalRepository<SonarrEpisodeCache> sonarrEpisodeCache,
            IMediaCacheService mediaCache,
            IServiceScopeFactory serviceScopeFactory,
            ILogger<MediaCleanupEngine> logger)
        {
            _settings = settings;
            _state = state;
            _radarrSettings = radarrSettings;
            _radarr4KSettings = radarr4KSettings;
            _sonarrSettings = sonarrSettings;
            _plexSettings = plexSettings;
            _movieRequests = movieRequests;
            _tvRequests = tvRequests;
            _currentUser = currentUser;
            _userManager = userManager;
            _radarr = radarr;
            _sonarr = sonarr;
            _plex = plex;
            _plexContent = plexContent;
            _radarrCache = radarrCache;
            _sonarrCache = sonarrCache;
            _sonarrEpisodeCache = sonarrEpisodeCache;
            _mediaCache = mediaCache;
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        public async Task<MediaCleanupOverview> GetOverview(
            RequestType? requestType = null,
            int? requestId = null,
            int? mediaId = null,
            bool includeMetrics = true,
            bool includeLastPlayed = true,
            CancellationToken cancellationToken = default)
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

            // Vote identities are moderation data. Only cleanup managers/admins receive
            // them; normal voters continue to see aggregate totals and their own vote.
            Dictionary<string, string> voterDisplayNames = null;
            if (permissions.CanManage)
            {
                var voterIds = active.Values
                    .SelectMany(x => x.Votes ?? new List<MediaCleanupVoteRecord>())
                    .Select(x => x.UserId)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .ToList();

                if (voterIds.Count > 0)
                {
                    var voters = await _userManager.Users
                        .Where(x => voterIds.Contains(x.Id))
                        .Select(x => new { x.Id, x.Alias, x.UserName })
                        .ToListAsync(cancellationToken);

                    voterDisplayNames = voters.ToDictionary(
                        x => x.Id,
                        x => string.IsNullOrWhiteSpace(x.Alias) ? x.UserName : x.Alias);
                }
                else
                {
                    voterDisplayNames = new Dictionary<string, string>();
                }
            }

            var result = new MediaCleanupOverview
            {
                Settings = settings,
                CanRequestRemoval = permissions.CanRequestRemoval,
                CanDeleteOwnMedia = permissions.CanDeleteOwnMedia,
                CanVote = permissions.CanVote,
                CanManage = permissions.CanManage
            };

            var includeMovies = !requestType.HasValue || requestType == RequestType.Movie;
            var includeTv = !requestType.HasValue || requestType == RequestType.TvShow;
            var movieSizes = includeMetrics && includeMovies ? await GetMovieSizes() : new Dictionary<int, long>();
            var tvSizes = includeMetrics && includeTv ? await GetTvSizes() : new Dictionary<int, long>();
            var plexLookup = includeLastPlayed ? await GetPlexContentLookup() : null;
            var plexKeys = new Dictionary<(RequestType Type, int RequestId), string>();
            var now = DateTime.UtcNow;

            if (includeMovies)
            {
                var movieQuery = _movieRequests.GetWithUser().Where(x => x.Available);
                if (requestId.HasValue)
                {
                    movieQuery = movieQuery.Where(x => x.Id == requestId.Value);
                }
                else if (mediaId.HasValue)
                {
                    // Media details pages have a stable TMDB id even when Ombi does not
                    // expose the request id to the current user. Resolve the shared request
                    // by provider id so community cleanup is not tied to request ownership.
                    movieQuery = movieQuery.Where(x => x.TheMovieDbId == mediaId.Value);
                }

                var movies = await movieQuery.OrderBy(x => x.Title).ToListAsync();
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

                    if (plexLookup != null)
                    {
                        var plexKey = plexLookup.FindMovie(movie.TheMovieDbId, movie.ImdbId);
                        if (!string.IsNullOrEmpty(plexKey))
                        {
                            plexKeys[(RequestType.Movie, movie.Id)] = plexKey;
                        }
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
                        Cleanup = ToViewModel(cleanup, user.Id, settings, voterDisplayNames)
                    });
                }
            }

            if (includeTv)
            {
                var tvQuery = _tvRequests.GetLite()
                    .Where(x => x.ChildRequests.Any() && x.ChildRequests.All(c => c.Available));
                if (requestId.HasValue)
                {
                    tvQuery = tvQuery.Where(x => x.Id == requestId.Value);
                }
                else if (mediaId.HasValue)
                {
                    // TV detail ids are TMDB ids in the current search model. Keep the
                    // legacy TVDB comparison as a fallback for older request records.
                    tvQuery = tvQuery.Where(x => x.ExternalProviderId == mediaId.Value || x.TvDbId == mediaId.Value);
                }

                var tvRequests = await tvQuery.OrderBy(x => x.Title).ToListAsync();
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

                    if (plexLookup != null)
                    {
                        var plexKey = plexLookup.FindSeries(tv.TvDbId, tv.ExternalProviderId, tv.ImdbId);
                        if (!string.IsNullOrEmpty(plexKey))
                        {
                            plexKeys[(RequestType.TvShow, tv.Id)] = plexKey;
                        }
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
                        Cleanup = ToViewModel(cleanup, user.Id, settings, voterDisplayNames)
                    });
                }
            }

            if (includeLastPlayed)
            {
                await PopulateLastPlayed(result.Items, plexKeys, cancellationToken);
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
                    QueueManagersPendingApprovalNotification(record, settings);
                    return Success("Removal request submitted for administrator approval.", record.Id);
                }

                record.Status = MediaCleanupStatus.ScheduledForDeletion;
                record.ScheduledForDeletionAt = DateTime.UtcNow;
                state.Requests.Add(record);

                // Immediate deletion is synchronous. Persist the final state once after the
                // deletion attempt to avoid tracking two GlobalSettings instances in the
                // same scoped SettingsContext.
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
                if (record.Status == MediaCleanupStatus.PendingAdminApproval)
                {
                    QueueManagersPendingApprovalNotification(record, settings);
                }

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

                var statusBeforeDeadlineEvaluation = record.Status;
                EvaluateCommunity(record, settings, DateTime.UtcNow);
                if (!IsVoteable(record))
                {
                    await SaveState(state);
                    if (statusBeforeDeadlineEvaluation != MediaCleanupStatus.PendingAdminApproval &&
                        record.Status == MediaCleanupStatus.PendingAdminApproval)
                    {
                        QueueManagersPendingApprovalNotification(record, settings);
                    }
                    return Fail("This cleanup request is no longer open for voting.", record.Id);
                }

                var previousStatus = record.Status;
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
                if (previousStatus != MediaCleanupStatus.PendingAdminApproval && record.Status == MediaCleanupStatus.PendingAdminApproval)
                {
                    QueueManagersPendingApprovalNotification(record, settings);
                }
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
                var newlyPendingApproval = new List<MediaCleanupRecord>();

                foreach (var record in state.Requests.Where(IsActive).ToList())
                {
                    if (record.Origin == MediaCleanupOrigin.Community)
                    {
                        var before = record.Status;
                        var beforeScheduled = record.ScheduledForDeletionAt;
                        EvaluateCommunity(record, settings, now);
                        changed |= before != record.Status || beforeScheduled != record.ScheduledForDeletionAt;
                        if (before != MediaCleanupStatus.PendingAdminApproval && record.Status == MediaCleanupStatus.PendingAdminApproval)
                        {
                            newlyPendingApproval.Add(record);
                        }
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

                foreach (var record in newlyPendingApproval)
                {
                    QueueManagersPendingApprovalNotification(record, settings);
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

        private void QueueManagersPendingApprovalNotification(MediaCleanupRecord record, MediaCleanupSettings cleanupSettings)
        {
            if (!cleanupSettings.NotifyManagersOnPendingApproval)
            {
                return;
            }

            // Approval email is a secondary notification. Do not hold the user's cleanup
            // HTTP request open while SMTP connects/sends. Use a fresh DI scope so the
            // background work never touches request-scoped services after they are disposed.
            var cleanupId = record.Id;
            var title = record.Title;
            var requestedByUserId = record.RequestedByUserId;
            var origin = record.Origin;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var emailSettingsService = scope.ServiceProvider.GetRequiredService<ISettingsService<EmailNotificationSettings>>();
                    var userManager = scope.ServiceProvider.GetRequiredService<OmbiUserManager>();
                    var emailProvider = scope.ServiceProvider.GetRequiredService<IEmailProvider>();

                    var emailSettings = await emailSettingsService.GetSettingsAsync();
                    if (emailSettings == null || !emailSettings.Enabled)
                    {
                        return;
                    }

                    var recipients = new List<OmbiUser>();
                    recipients.AddRange(await userManager.GetUsersInRoleAsync(OmbiRoles.Admin));
                    recipients.AddRange(await userManager.GetUsersInRoleAsync(OmbiRoles.PowerUser));
                    recipients.AddRange(await userManager.GetUsersInRoleAsync(OmbiRoles.ManageMediaCleanup));

                    var requester = string.IsNullOrEmpty(requestedByUserId)
                        ? null
                        : await userManager.FindByIdAsync(requestedByUserId);
                    var requesterName = requester?.UserAlias ?? "An Ombi user";
                    var encodedTitle = System.Net.WebUtility.HtmlEncode(title ?? "Media");
                    var encodedRequester = System.Net.WebUtility.HtmlEncode(requesterName);
                    var source = origin == MediaCleanupOrigin.OwnRequest
                        ? $"{encodedRequester} requested removal of this title."
                        : "A community cleanup vote reached the configured threshold and now requires approval.";
                    var plainSource = origin == MediaCleanupOrigin.OwnRequest
                        ? $"{requesterName} requested removal of this title."
                        : "A community cleanup vote reached the configured threshold and now requires approval.";

                    foreach (var recipient in recipients
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Email))
                        .GroupBy(x => x.Id)
                        .Select(x => x.First()))
                    {
                        await emailProvider.SendAdHoc(new NotificationMessage
                        {
                            To = recipient.Email,
                            Subject = $"Media cleanup approval required: {title}",
                            Message = $"<p><strong>{encodedTitle}</strong> is waiting for Media Cleanup approval.</p><p>{source}</p><p>Open Ombi and go to <strong>Media Cleanup</strong> to approve or reject it.</p>",
                            Other =
                            {
                                ["PlainTextBody"] = $"{title} is waiting for Media Cleanup approval. {plainSource} Open Ombi and go to Media Cleanup to approve or reject it."
                            }
                        }, emailSettings);
                    }
                }
                catch (Exception ex)
                {
                    // Approval workflow must not fail merely because SMTP is unavailable.
                    _logger.LogWarning(ex, "Could not send Media Cleanup approval notification for {Title} ({CleanupId})", title, cleanupId);
                }
            });
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
                    // The details page determines Requested by provider id, not by the cleanup
                    // record's single request id. Remove every Ombi request row for this movie so
                    // historical/duplicate rows cannot leave the title stuck as Requested.
                    var movieRequests = await _movieRequests.GetAll()
                        .Where(x => x.TheMovieDbId == record.TheMovieDbId || x.Id == record.MediaRequestId)
                        .ToListAsync();
                    if (movieRequests.Count > 0)
                    {
                        await _movieRequests.DeleteRange(movieRequests);
                    }
                }
                else
                {
                    // Same rule for TV: once the actual series is removed, all Ombi request
                    // parents for that provider identity must be removed or ExistingRule can still
                    // report the series as requested.
                    var tvRequests = await _tvRequests.Get()
                        .Where(x => x.Id == record.MediaRequestId ||
                                    (record.TheMovieDbId > 0 && x.ExternalProviderId == record.TheMovieDbId) ||
                                    (record.TvDbId > 0 && x.TvDbId == record.TvDbId))
                        .ToListAsync();
                    if (tvRequests.Count > 0)
                    {
                        await _tvRequests.DeleteRange(tvRequests);
                    }
                }

                // Plex availability is backed by Ombi's external content cache, not a live Plex lookup.
                // A partial Plex sync is additive and can leave a deleted title marked Available, so
                // remove the matching cached row immediately after a successful cleanup.
                await RemovePlexAvailabilityCache(record);
                await RemoveArrSearchCache(record);
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

        private async Task RemoveArrSearchCache(MediaCleanupRecord record)
        {
            try
            {
                if (record.RequestType == RequestType.Movie)
                {
                    var matches = await _radarrCache.GetAll()
                        .Where(x => x.TheMovieDbId == record.TheMovieDbId)
                        .ToListAsync();
                    if (matches.Count > 0)
                    {
                        await _radarrCache.DeleteRange(matches);
                        _logger.LogInformation(
                            "Removed {Count} stale Radarr cache row(s) for '{Title}' (TMDB {TmdbId})",
                            matches.Count,
                            record.Title,
                            record.TheMovieDbId);
                    }
                    return;
                }

                if (record.RequestType == RequestType.TvShow)
                {
                    var seriesMatches = await _sonarrCache.GetAll()
                        .Where(x =>
                            (record.TvDbId > 0 && x.TvDbId == record.TvDbId) ||
                            (record.TheMovieDbId > 0 && x.TheMovieDbId == record.TheMovieDbId))
                        .ToListAsync();

                    var episodeMatches = await _sonarrEpisodeCache.GetAll()
                        .Where(x =>
                            (record.TvDbId > 0 && x.TvDbId == record.TvDbId) ||
                            (record.TheMovieDbId > 0 && x.MovieDbId == record.TheMovieDbId))
                        .ToListAsync();

                    if (episodeMatches.Count > 0)
                    {
                        await _sonarrEpisodeCache.DeleteRange(episodeMatches);
                    }
                    if (seriesMatches.Count > 0)
                    {
                        await _sonarrCache.DeleteRange(seriesMatches);
                    }

                    if (seriesMatches.Count > 0 || episodeMatches.Count > 0)
                    {
                        _logger.LogInformation(
                            "Removed stale Sonarr cache for '{Title}': {SeriesCount} series row(s), {EpisodeCount} episode row(s)",
                            record.Title,
                            seriesMatches.Count,
                            episodeMatches.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                // The destructive *arr operation and Ombi request removal have already succeeded.
                // A stale external cache should never turn that successful cleanup into a false failure.
                _logger.LogWarning(ex,
                    "Could not remove *arr search cache for {RequestType} '{Title}'",
                    record.RequestType,
                    record.Title);
            }
        }

        private async Task RemovePlexAvailabilityCache(MediaCleanupRecord record)
        {
            try
            {
                var plexSettings = await _plexSettings.GetSettingsAsync();
                if (plexSettings?.Enable != true || plexSettings.Servers == null || plexSettings.Servers.Count == 0)
                {
                    return;
                }

                // Plex rating keys and cached rows are not associated with a specific configured
                // Plex server in Ombi. Purging provider matches is therefore only unambiguous when
                // one Plex server is configured. A normal/full media database refresh remains the
                // safe reconciliation path for multi-server installations.
                if (plexSettings.Servers.Count != 1)
                {
                    _logger.LogWarning(
                        "Skipping direct Plex availability cache cleanup for {Title} because {ServerCount} Plex servers are configured",
                        record.Title,
                        plexSettings.Servers.Count);
                    return;
                }

                var mediaType = record.RequestType == RequestType.Movie ? MediaType.Movie : MediaType.Series;
                var tmdbId = record.TheMovieDbId > 0 ? record.TheMovieDbId.ToString() : null;
                var tvdbId = record.TvDbId > 0 ? record.TvDbId.ToString() : null;
                var requestId = record.MediaRequestId;

                var matches = _plexContent.GetWhereContentByCustom(x =>
                        x.Type == mediaType &&
                        ((tmdbId != null && x.TheMovieDbId == tmdbId) ||
                         (tvdbId != null && x.TvDbId == tvdbId) ||
                         x.RequestId == requestId))
                    .Select(x => x.Id)
                    .Distinct()
                    .ToList();

                foreach (var id in matches)
                {
                    // GetFirstContentByCustom includes seasons and episodes, which lets the
                    // repository remove the complete cached graph without leaving FK rows behind.
                    var content = await _plexContent.GetFirstContentByCustom(x => x.Id == id);
                    if (content == null)
                    {
                        continue;
                    }

                    await _plexContent.DeleteContent(content);
                    _logger.LogInformation(
                        "Removed stale Plex availability cache for {RequestType} '{Title}' (Plex content {PlexContentId})",
                        record.RequestType,
                        record.Title,
                        id);
                }
            }
            catch (Exception ex)
            {
                // The destructive *arr operation and Ombi request removal have already succeeded.
                // A cache-maintenance failure must not turn that successful cleanup into a false
                // failure response; a full media database refresh can reconcile it later.
                _logger.LogWarning(ex,
                    "Could not remove Plex availability cache for {RequestType} '{Title}'",
                    record.RequestType,
                    record.Title);
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
                    // Sonarr v3/v4 returns the aggregate series size in the nested
                    // statistics object. Keep the root-level value as a fallback for
                    // older Sonarr versions/responses.
                    var sizeOnDisk = series.statistics?.sizeOnDisk > 0
                        ? series.statistics.sizeOnDisk
                        : series.sizeOnDisk;
                    result[series.tvdbId] = sizeOnDisk;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Sonarr sizes for the media cleanup page");
            }

            return result;
        }

        private async Task<PlexContentLookup> GetPlexContentLookup()
        {
            try
            {
                var content = await _plexContent.GetAll().AsNoTracking().ToListAsync();
                return new PlexContentLookup(content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Plex content mappings for media cleanup play history");
                return new PlexContentLookup(Array.Empty<PlexServerContent>());
            }
        }

        private async Task PopulateLastPlayed(
            IEnumerable<MediaCleanupItemViewModel> items,
            IReadOnlyDictionary<(RequestType Type, int RequestId), string> plexKeys,
            CancellationToken cancellationToken)
        {
            if (plexKeys.Count == 0)
            {
                return;
            }

            var settings = await _plexSettings.GetSettingsAsync();
            var servers = settings?.Enable == true
                ? settings.Servers?.Where(x => x != null && !string.IsNullOrWhiteSpace(x.PlexAuthToken) && !string.IsNullOrWhiteSpace(x.Ip)).ToList()
                : null;

            // PlexServerContent currently does not record which Plex server supplied a rating key.
            // Querying more than one configured server could therefore match an unrelated ratingKey.
            if (servers == null || servers.Count != 1)
            {
                if (servers?.Count > 1)
                {
                    _logger.LogDebug("Media cleanup last-played lookup is skipped when multiple Plex servers are configured because cached Plex rating keys are not server-scoped");
                }
                return;
            }

            var server = servers[0];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var concurrency = new SemaphoreSlim(6, 6);
            var tasks = items
                .Where(item => plexKeys.ContainsKey((item.RequestType, item.RequestId)))
                .Select(async item =>
                {
                    var entered = false;
                    try
                    {
                        await concurrency.WaitAsync(timeout.Token);
                        entered = true;
                        var key = plexKeys[(item.RequestType, item.RequestId)];
                        var history = await _plex.GetHistory(server.PlexAuthToken, server.FullUri, key, timeout.Token);
                        var latest = history?.MediaContainer?.Metadata?.FirstOrDefault();

                        // A successful empty response means Plex has no recorded play for this title.
                        item.LastPlayedKnown = true;
                        if (latest?.viewedAt > 0)
                        {
                            item.LastPlayedAt = DateTimeOffset.FromUnixTimeSeconds(latest.viewedAt.Value).UtcDateTime;
                        }
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        // Leave LastPlayedKnown false. The cleanup page treats this as Unknown.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not load Plex play history for cleanup item {Title}", item.Title);
                    }
                    finally
                    {
                        if (entered)
                        {
                            concurrency.Release();
                        }
                    }
                });

            await Task.WhenAll(tasks);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Media cleanup last-played lookup reached its 15 second time limit; unresolved titles will remain Unknown");
            }
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

            // The configured voting period is a minimum voting window, not merely a
            // deadline by which the threshold must be reached. Never advance a
            // community cleanup request to approval or deletion before VotingEndsAt,
            // even when the current votes already satisfy the threshold/margin.
            //
            // This also repairs requests that an older build advanced early: while
            // their original voting window is still open, put them back into Voting.
            if (record.VotingEndsAt.HasValue && record.VotingEndsAt.Value > now)
            {
                record.Status = MediaCleanupStatus.Voting;
                record.ScheduledForDeletionAt = null;
                record.ApprovedByUserId = null;
                return;
            }

            // Voting has ended. Evaluate the final vote snapshot exactly once the
            // configured window has elapsed. Votes can no longer change after this.
            var keepVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Keep);
            var deleteVotes = record.Votes.Count(x => x.Vote == MediaCleanupVoteType.Delete);
            var requesterVeto = settings.RequesterCanVeto && record.Votes.Any(x =>
                x.Vote == MediaCleanupVoteType.Keep && record.OwnerUserIds.Contains(x.UserId));
            var thresholdMet = !requesterVeto &&
                               deleteVotes >= Math.Max(1, settings.MinimumDeleteVotes) &&
                               deleteVotes - keepVotes >= Math.Max(0, settings.RequiredVoteMargin);

            if (!thresholdMet)
            {
                record.ApprovedByUserId = null;
                record.Status = MediaCleanupStatus.Rejected;
                record.ScheduledForDeletionAt = null;
                return;
            }

            if (settings.CommunityCleanup == CommunityCleanupMode.AutomaticAfterThreshold)
            {
                if (record.Status != MediaCleanupStatus.ScheduledForDeletion)
                {
                    record.Status = MediaCleanupStatus.ScheduledForDeletion;
                    record.ScheduledForDeletionAt = now.AddDays(Math.Max(0, settings.GracePeriodDays));
                }
                return;
            }

            if (settings.CommunityCleanup == CommunityCleanupMode.AdminApproval)
            {
                if (!string.IsNullOrEmpty(record.ApprovedByUserId))
                {
                    record.Status = MediaCleanupStatus.ScheduledForDeletion;
                }
                else
                {
                    record.Status = MediaCleanupStatus.PendingAdminApproval;
                    record.ScheduledForDeletionAt = null;
                }
            }
        }

        private MediaCleanupRequestViewModel ToViewModel(
            MediaCleanupRecord record,
            string userId,
            MediaCleanupSettings settings,
            IReadOnlyDictionary<string, string> voterDisplayNames)
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
                FailureReason = record.FailureReason,
                Voters = voterDisplayNames == null
                    ? new List<MediaCleanupVoterViewModel>()
                    : record.Votes
                        .OrderBy(x => x.Date)
                        .Select(x => new MediaCleanupVoterViewModel
                        {
                            DisplayName = voterDisplayNames.TryGetValue(x.UserId, out var displayName)
                                ? displayName
                                : "Former or unknown user",
                            Vote = x.Vote,
                            Date = x.Date,
                            IsRequester = record.OwnerUserIds.Contains(x.UserId)
                        })
                        .ToList()
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
            return record.Status == MediaCleanupStatus.Voting &&
                   (!record.VotingEndsAt.HasValue || record.VotingEndsAt.Value > DateTime.UtcNow);
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

        private sealed class PlexContentLookup
        {
            private readonly Dictionary<string, string> _movieByTmdb;
            private readonly Dictionary<string, string> _movieByImdb;
            private readonly Dictionary<string, string> _seriesByTvdb;
            private readonly Dictionary<string, string> _seriesByTmdb;
            private readonly Dictionary<string, string> _seriesByImdb;

            public PlexContentLookup(IEnumerable<PlexServerContent> content)
            {
                var items = content?.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToList() ?? new List<PlexServerContent>();
                _movieByTmdb = Build(items.Where(x => x.Type == MediaType.Movie), x => x.TheMovieDbId);
                _movieByImdb = Build(items.Where(x => x.Type == MediaType.Movie), x => x.ImdbId);
                _seriesByTvdb = Build(items.Where(x => x.Type == MediaType.Series), x => x.TvDbId);
                _seriesByTmdb = Build(items.Where(x => x.Type == MediaType.Series), x => x.TheMovieDbId);
                _seriesByImdb = Build(items.Where(x => x.Type == MediaType.Series), x => x.ImdbId);
            }

            public string FindMovie(int tmdbId, string imdbId)
            {
                if (tmdbId > 0 && _movieByTmdb.TryGetValue(tmdbId.ToString(), out var key))
                {
                    return key;
                }
                return !string.IsNullOrWhiteSpace(imdbId) && _movieByImdb.TryGetValue(imdbId, out key) ? key : null;
            }

            public string FindSeries(int tvdbId, int tmdbId, string imdbId)
            {
                if (tvdbId > 0 && _seriesByTvdb.TryGetValue(tvdbId.ToString(), out var key))
                {
                    return key;
                }
                if (tmdbId > 0 && _seriesByTmdb.TryGetValue(tmdbId.ToString(), out key))
                {
                    return key;
                }
                return !string.IsNullOrWhiteSpace(imdbId) && _seriesByImdb.TryGetValue(imdbId, out key) ? key : null;
            }

            private static Dictionary<string, string> Build(IEnumerable<PlexServerContent> content, Func<PlexServerContent, string> idSelector)
            {
                return content
                    .Select(x => new { Id = idSelector(x), x.Key })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Key, StringComparer.OrdinalIgnoreCase);
            }
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
