using System.Collections.Generic;
using System.Threading.Tasks;
using Ombi.Core.Models.Requests;

namespace Ombi.Core.Services
{
    public interface IQualityProfileSelectionService
    {
        Task<IReadOnlyCollection<QualityProfileOption>> GetRadarrProfiles(bool is4K);
        Task<IReadOnlyCollection<QualityProfileOption>> GetSonarrProfiles();
        Task<bool> IsValidRadarrProfile(int profileId, bool is4K);
        Task<bool> IsValidSonarrProfile(int profileId);
    }
}
