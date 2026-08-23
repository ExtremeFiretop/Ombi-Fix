using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Ombi.Core.Authentication;
using Ombi.Store.Entities;

namespace Ombi
{
    /// <summary>
    /// Records recent authenticated Ombi activity without changing LastLoggedIn.
    /// Activity writes are throttled so normal page/API traffic does not write to the
    /// user table on every request.
    /// </summary>
    public sealed class UserActivityMiddleware
    {
        private static readonly TimeSpan ActivityWriteInterval = TimeSpan.FromMinutes(1);
        private const string ActivityCachePrefix = "user-last-active:";
        private readonly RequestDelegate _next;
        private readonly ILogger<UserActivityMiddleware> _logger;

        public UserActivityMiddleware(RequestDelegate next, ILogger<UserActivityMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, OmbiUserManager userManager, IMemoryCache cache)
        {
            await RecordActivity(context, userManager, cache);
            await _next(context);
        }

        private async Task RecordActivity(HttpContext context, OmbiUserManager userManager, IMemoryCache cache)
        {
            if (!context.Request.Path.StartsWithSegments(new PathString("/api")) ||
                context.User?.Identity?.IsAuthenticated != true ||
                IsApiKeyRequest(context))
            {
                return;
            }

            var userName = context.User.Identity.Name;
            if (string.IsNullOrWhiteSpace(userName) ||
                string.Equals(userName, "API", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                         context.User.FindFirst("Id")?.Value;
            var cacheIdentity = string.IsNullOrWhiteSpace(userId) ? userName.ToUpperInvariant() : userId;
            var cacheKey = ActivityCachePrefix + cacheIdentity;

            if (cache.TryGetValue(cacheKey, out _))
            {
                return;
            }

            // Set this before the database work so a burst of parallel API calls from one
            // page load does not all attempt an activity update at the same time.
            cache.Set(cacheKey, true, ActivityWriteInterval);

            try
            {
                OmbiUser user;
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    user = await userManager.Users.FirstOrDefaultAsync(x => x.Id == userId);
                }
                else
                {
                    var normalizedUserName = userName.ToUpperInvariant();
                    user = await userManager.Users.FirstOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName);
                }

                if (user == null || user.IsSystemUser)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if (user.LastActive.HasValue && now - user.LastActive.Value < ActivityWriteInterval)
                {
                    return;
                }

                user.LastActive = now;
                var result = await userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    _logger.LogWarning("Could not update LastActive for Ombi user {UserName}: {Errors}",
                        user.UserName,
                        string.Join("; ", result.Errors.Select(x => x.Description)));
                    cache.Remove(cacheKey);
                }
            }
            catch (Exception ex)
            {
                // Activity tracking must never prevent the user's real request from running.
                cache.Remove(cacheKey);
                _logger.LogWarning(ex, "Could not update LastActive for Ombi user {UserName}", userName);
            }
        }

        private static bool IsApiKeyRequest(HttpContext context)
        {
            return context.Request.Headers.Keys.Any(x => string.Equals(x, "ApiKey", StringComparison.OrdinalIgnoreCase)) ||
                   context.Request.Query.ContainsKey("apikey");
        }
    }
}
