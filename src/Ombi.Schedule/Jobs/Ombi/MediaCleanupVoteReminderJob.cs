using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ombi.Core.Settings;
using Ombi.Helpers;
using Ombi.Notifications;
using Ombi.Notifications.Models;
using Ombi.Settings.Settings.Models;
using Ombi.Settings.Settings.Models.Notifications;
using Ombi.Store.Entities;
using Quartz;

namespace Ombi.Schedule.Jobs.Ombi
{
    public class MediaCleanupVoteReminderJob : IMediaCleanupVoteReminderJob
    {
        private readonly ISettingsService<MediaCleanupSettings> _cleanupSettings;
        private readonly ISettingsService<MediaCleanupState> _cleanupState;
        private readonly ISettingsService<EmailNotificationSettings> _emailSettings;
        private readonly ISettingsService<CustomizationSettings> _customizationSettings;
        private readonly ISettingsService<OmbiSettings> _ombiSettings;
        private readonly UserManager<OmbiUser> _userManager;
        private readonly IEmailProvider _emailProvider;
        private readonly ILogger<MediaCleanupVoteReminderJob> _logger;

        public MediaCleanupVoteReminderJob(
            ISettingsService<MediaCleanupSettings> cleanupSettings,
            ISettingsService<MediaCleanupState> cleanupState,
            ISettingsService<EmailNotificationSettings> emailSettings,
            ISettingsService<CustomizationSettings> customizationSettings,
            ISettingsService<OmbiSettings> ombiSettings,
            UserManager<OmbiUser> userManager,
            IEmailProvider emailProvider,
            ILogger<MediaCleanupVoteReminderJob> logger)
        {
            _cleanupSettings = cleanupSettings;
            _cleanupState = cleanupState;
            _emailSettings = emailSettings;
            _customizationSettings = customizationSettings;
            _ombiSettings = ombiSettings;
            _userManager = userManager;
            _emailProvider = emailProvider;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _cleanupSettings.ClearCache();
            var settings = await _cleanupSettings.GetSettingsAsync();
            if (settings == null ||
                settings.CommunityCleanup == CommunityCleanupMode.Off ||
                !settings.NotifyVotersOnPendingVotes)
            {
                return;
            }

            _emailSettings.ClearCache();
            var emailSettings = await _emailSettings.GetSettingsAsync();
            if (emailSettings == null || !emailSettings.Enabled)
            {
                _logger.LogDebug("Skipping Media Cleanup vote reminder because email notifications are disabled.");
                return;
            }

            _cleanupState.ClearCache();
            var state = await _cleanupState.GetSettingsAsync();
            var now = DateTime.UtcNow;
            var activeVotes = state?.Requests?
                .Where(x => x != null &&
                            x.Origin == MediaCleanupOrigin.Community &&
                            x.Status == MediaCleanupStatus.Voting &&
                            (!x.VotingEndsAt.HasValue || x.VotingEndsAt.Value > now))
                .ToList() ?? new List<MediaCleanupRecord>();

            if (activeVotes.Count == 0)
            {
                return;
            }

            // Resolve voters the same way the Media Cleanup engine evaluates permissions:
            // inspect each Ombi user's actual assigned roles. This avoids relying on a
            // GetUsersInRoleAsync union that can leave otherwise vote-capable users out of
            // the scheduled reminder audience on some installations.
            var allUsers = await _userManager.Users.ToListAsync();
            var eligibleUsers = new List<OmbiUser>();
            var roleLookupFailures = 0;

            foreach (var user in allUsers.Where(x => x != null && !x.IsSystemUser))
            {
                try
                {
                    var roles = await _userManager.GetRolesAsync(user);
                    if (HasCleanupVotingRole(roles))
                    {
                        eligibleUsers.Add(user);
                    }
                }
                catch (Exception ex)
                {
                    roleLookupFailures++;
                    _logger.LogWarning(ex,
                        "Could not resolve Media Cleanup voting roles for {UserName} ({UserId})",
                        user.UserName, user.Id);
                }
            }

            var eligibleWithEmail = eligibleUsers
                .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .ToList();

            _logger.LogInformation(
                "Media Cleanup vote reminder starting. ActiveVotes={ActiveVotes}, TotalUsers={TotalUsers}, EligibleUsers={EligibleUsers}, EligibleUsersWithEmail={EligibleUsersWithEmail}, RoleLookupFailures={RoleLookupFailures}",
                activeVotes.Count, allUsers.Count, eligibleUsers.Count, eligibleWithEmail.Count, roleLookupFailures);

            foreach (var user in eligibleUsers.Where(x => string.IsNullOrWhiteSpace(x.Email)))
            {
                _logger.LogDebug(
                    "Skipping Media Cleanup vote reminder for {UserName} ({UserId}): no email address is configured.",
                    user.UserName, user.Id);
            }

            _customizationSettings.ClearCache();
            var customization = await _customizationSettings.GetSettingsAsync();
            _ombiSettings.ClearCache();
            var ombiSettings = await _ombiSettings.GetSettingsAsync();
            var cleanupUrl = BuildCleanupUrl(customization?.ApplicationUrl, ombiSettings?.BaseUrl);
            _logger.LogDebug("Media Cleanup vote reminder URL resolved to {CleanupUrl}", cleanupUrl);
            var usersWithPendingVotes = 0;
            var attempted = 0;
            var sent = 0;
            var failed = 0;

            foreach (var user in eligibleWithEmail)
            {
                var pending = activeVotes
                    .Where(x => x.Votes == null || x.Votes.All(v => v.UserId != user.Id))
                    .OrderBy(x => x.VotingEndsAt ?? DateTime.MaxValue)
                    .ThenBy(x => x.Title)
                    .ToList();

                if (pending.Count == 0)
                {
                    _logger.LogDebug(
                        "Skipping Media Cleanup vote reminder for {UserName} ({UserId}): all active votes have already been answered.",
                        user.UserName, user.Id);
                    continue;
                }

                usersWithPendingVotes++;
                attempted++;
                var email = user.Email.Trim();

                try
                {
                    var message = BuildMessage(user, pending, cleanupUrl, email);
                    await _emailProvider.SendAdHoc(message, emailSettings);
                    sent++;
                    _logger.LogInformation(
                        "Sent Media Cleanup vote reminder to {UserName} ({UserId}) with {PendingVotes} pending vote(s).",
                        user.UserName, user.Id, pending.Count);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex,
                        "Could not send Media Cleanup vote reminder to {UserName} ({UserId}) at {Email}",
                        user.UserName, user.Id, email);
                }
            }

            _logger.LogInformation(
                "Media Cleanup vote reminder completed. ActiveVotes={ActiveVotes}, EligibleUsers={EligibleUsers}, EligibleUsersWithEmail={EligibleUsersWithEmail}, UsersWithPendingVotes={UsersWithPendingVotes}, Attempted={Attempted}, Sent={Sent}, Failed={Failed}, RoleLookupFailures={RoleLookupFailures}",
                activeVotes.Count, eligibleUsers.Count, eligibleWithEmail.Count, usersWithPendingVotes, attempted, sent, failed, roleLookupFailures);
        }

        private static string BuildCleanupUrl(string applicationUrl, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(applicationUrl))
            {
                return null;
            }

            var normalizedApplicationUrl = applicationUrl.Trim().TrimEnd('/');
            var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? string.Empty
                : "/" + baseUrl.Trim().Trim('/');

            // ApplicationUrl is commonly configured as just the public origin while
            // OmbiSettings.BaseUrl contains a reverse-proxy path such as /requests.
            // Do not append that path twice when ApplicationUrl already includes it.
            if (normalizedBaseUrl.Length > 0 &&
                normalizedApplicationUrl.EndsWith(normalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                normalizedBaseUrl = string.Empty;
            }

            return $"{normalizedApplicationUrl}{normalizedBaseUrl}/cleanup";
        }

        private static bool HasCleanupVotingRole(IEnumerable<string> roles)
        {
            return roles.Any(role =>
                string.Equals(role, OmbiRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, OmbiRoles.PowerUser, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, OmbiRoles.VoteOnMediaCleanup, StringComparison.OrdinalIgnoreCase));
        }

        private static NotificationMessage BuildMessage(OmbiUser user, IReadOnlyCollection<MediaCleanupRecord> pending, string cleanupUrl, string email)
        {
            var displayName = WebUtility.HtmlEncode(user.UserName ?? "Ombi user");
            var html = new StringBuilder();
            html.Append($"<p>Hi {displayName},</p>");
            html.Append($"<p>You have <strong>{pending.Count}</strong> active Media Cleanup vote{(pending.Count == 1 ? string.Empty : "s")} waiting for your response.</p>");
            html.Append("<ul>");

            foreach (var item in pending)
            {
                var title = WebUtility.HtmlEncode(item.Title ?? "Media");
                var deadline = item.VotingEndsAt.HasValue
                    ? $" — voting ends {item.VotingEndsAt.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC"
                    : string.Empty;
                var deleteVotes = item.Votes?.Count(x => x.Vote == MediaCleanupVoteType.Delete) ?? 0;
                var keepVotes = item.Votes?.Count(x => x.Vote == MediaCleanupVoteType.Keep) ?? 0;
                html.Append($"<li><strong>{title}</strong> — {deleteVotes} Remove / {keepVotes} Keep{deadline}</li>");
            }

            html.Append("</ul>");
            if (!string.IsNullOrWhiteSpace(cleanupUrl))
            {
                var encodedUrl = WebUtility.HtmlEncode(cleanupUrl);
                html.Append($"<p><a href=\"{encodedUrl}\">Open Media Cleanup to vote</a></p>");
            }
            else
            {
                html.Append("<p>Open Ombi and go to <strong>Media Cleanup</strong> to vote.</p>");
            }

            var plain = new StringBuilder();
            plain.AppendLine($"You have {pending.Count} active Media Cleanup vote{(pending.Count == 1 ? string.Empty : "s")} waiting for your response.");
            foreach (var item in pending)
            {
                var deadline = item.VotingEndsAt.HasValue
                    ? $"; voting ends {item.VotingEndsAt.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC"
                    : string.Empty;
                var deleteVotes = item.Votes?.Count(x => x.Vote == MediaCleanupVoteType.Delete) ?? 0;
                var keepVotes = item.Votes?.Count(x => x.Vote == MediaCleanupVoteType.Keep) ?? 0;
                plain.AppendLine($"- {item.Title}: {deleteVotes} Remove / {keepVotes} Keep{deadline}");
            }
            plain.AppendLine(!string.IsNullOrWhiteSpace(cleanupUrl)
                ? $"Vote here: {cleanupUrl}"
                : "Open Ombi and go to Media Cleanup to vote.");

            return new NotificationMessage
            {
                To = email,
                Subject = $"Media Cleanup: {pending.Count} vote{(pending.Count == 1 ? string.Empty : "s")} waiting for you",
                Message = html.ToString(),
                Other =
                {
                    ["PlainTextBody"] = plain.ToString()
                }
            };
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
