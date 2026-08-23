using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
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
        private readonly UserManager<OmbiUser> _userManager;
        private readonly IEmailProvider _emailProvider;
        private readonly ILogger<MediaCleanupVoteReminderJob> _logger;

        public MediaCleanupVoteReminderJob(
            ISettingsService<MediaCleanupSettings> cleanupSettings,
            ISettingsService<MediaCleanupState> cleanupState,
            ISettingsService<EmailNotificationSettings> emailSettings,
            ISettingsService<CustomizationSettings> customizationSettings,
            UserManager<OmbiUser> userManager,
            IEmailProvider emailProvider,
            ILogger<MediaCleanupVoteReminderJob> logger)
        {
            _cleanupSettings = cleanupSettings;
            _cleanupState = cleanupState;
            _emailSettings = emailSettings;
            _customizationSettings = customizationSettings;
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

            var recipients = new List<OmbiUser>();
            recipients.AddRange(await _userManager.GetUsersInRoleAsync(OmbiRoles.Admin));
            recipients.AddRange(await _userManager.GetUsersInRoleAsync(OmbiRoles.PowerUser));
            recipients.AddRange(await _userManager.GetUsersInRoleAsync(OmbiRoles.VoteOnMediaCleanup));

            _customizationSettings.ClearCache();
            var customization = await _customizationSettings.GetSettingsAsync();
            var cleanupUrl = customization?.AddToUrl("cleanup");

            foreach (var user in recipients
                .Where(x => x != null && !x.IsSystemUser && !string.IsNullOrWhiteSpace(x.Email))
                .GroupBy(x => x.Id)
                .Select(x => x.First()))
            {
                var pending = activeVotes
                    .Where(x => x.Votes == null || x.Votes.All(v => v.UserId != user.Id))
                    .OrderBy(x => x.VotingEndsAt ?? DateTime.MaxValue)
                    .ThenBy(x => x.Title)
                    .ToList();

                if (pending.Count == 0)
                {
                    continue;
                }

                try
                {
                    var message = BuildMessage(user, pending, cleanupUrl);
                    await _emailProvider.SendAdHoc(message, emailSettings);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not send Media Cleanup vote reminder to {UserId} ({Email})",
                        user.Id, user.Email);
                }
            }
        }

        private static NotificationMessage BuildMessage(OmbiUser user, IReadOnlyCollection<MediaCleanupRecord> pending, string cleanupUrl)
        {
            var displayName = WebUtility.HtmlEncode(user.UserAlias ?? user.UserName ?? "Ombi user");
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
                To = user.Email,
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
