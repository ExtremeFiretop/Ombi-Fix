using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Core.Models.Requests;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;

namespace Ombi.Core.Services
{
    public class QualityProfileSelectionService : IQualityProfileSelectionService
    {
        private readonly ISettingsService<RadarrSettings> _radarrSettings;
        private readonly ISettingsService<Radarr4KSettings> _radarr4KSettings;
        private readonly ISettingsService<SonarrSettings> _sonarrSettings;
        private readonly IRadarrV3Api _radarrApi;
        private readonly ISonarrV3Api _sonarrApi;
        private readonly ILogger<QualityProfileSelectionService> _logger;

        public QualityProfileSelectionService(
            ISettingsService<RadarrSettings> radarrSettings,
            ISettingsService<Radarr4KSettings> radarr4KSettings,
            ISettingsService<SonarrSettings> sonarrSettings,
            IRadarrV3Api radarrApi,
            ISonarrV3Api sonarrApi,
            ILogger<QualityProfileSelectionService> logger)
        {
            _radarrSettings = radarrSettings;
            _radarr4KSettings = radarr4KSettings;
            _sonarrSettings = sonarrSettings;
            _radarrApi = radarrApi;
            _sonarrApi = sonarrApi;
            _logger = logger;
        }

        public async Task<IReadOnlyCollection<QualityProfileOption>> GetRadarrProfiles(bool is4K)
        {
            try
            {
                RadarrSettings settings = is4K
                    ? await _radarr4KSettings.GetSettingsAsync()
                    : await _radarrSettings.GetSettingsAsync();

                if (settings == null || !settings.Enabled)
                {
                    return Array.Empty<QualityProfileOption>();
                }

                var profiles = await _radarrApi.GetProfiles(settings.ApiKey, settings.FullUri);
                if (profiles == null)
                {
                    return Array.Empty<QualityProfileOption>();
                }

                return profiles
                    .Where(x => x != null && x.id > 0)
                    .Select(x => new QualityProfileOption { Id = x.id, Name = x.name })
                    .OrderBy(x => x.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load selectable {RadarrType} quality profiles", is4K ? "Radarr 4K" : "Radarr");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<QualityProfileOption>> GetSonarrProfiles()
        {
            try
            {
                var settings = await _sonarrSettings.GetSettingsAsync();
                if (settings == null || !settings.Enabled)
                {
                    return Array.Empty<QualityProfileOption>();
                }

                var profiles = await _sonarrApi.GetProfiles(settings.ApiKey, settings.FullUri);
                if (profiles == null)
                {
                    return Array.Empty<QualityProfileOption>();
                }

                return profiles
                    .Where(x => x != null && x.id > 0)
                    .Select(x => new QualityProfileOption { Id = x.id, Name = x.name })
                    .OrderBy(x => x.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load selectable Sonarr quality profiles");
                throw;
            }
        }

        public async Task<bool> IsValidRadarrProfile(int profileId, bool is4K)
        {
            if (profileId == 0)
            {
                return true;
            }
            if (profileId < 0)
            {
                return false;
            }

            return (await GetRadarrProfiles(is4K)).Any(x => x.Id == profileId);
        }

        public async Task<bool> IsValidSonarrProfile(int profileId)
        {
            if (profileId == 0)
            {
                return true;
            }
            if (profileId < 0)
            {
                return false;
            }

            return (await GetSonarrProfiles()).Any(x => x.Id == profileId);
        }
    }
}
