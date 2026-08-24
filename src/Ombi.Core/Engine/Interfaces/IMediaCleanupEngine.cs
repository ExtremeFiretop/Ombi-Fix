using System.Threading;
using System.Threading.Tasks;
using Ombi.Core.Models.MediaCleanup;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities;

namespace Ombi.Core.Engine.Interfaces
{
    public interface IMediaCleanupEngine
    {
        Task<MediaCleanupOverview> GetOverview(
            RequestType? requestType = null,
            int? requestId = null,
            int? mediaId = null,
            bool includeMetrics = true,
            bool includeLastPlayed = true,
            CancellationToken cancellationToken = default);
        Task<MediaCleanupTvSelectionViewModel> GetTvSelection(int requestId);
        Task<MediaCleanupActionResult> RequestOwnRemoval(RequestType requestType, int requestId, MediaCleanupSelection selection = null);
        Task<MediaCleanupActionResult> Nominate(RequestType requestType, int requestId, MediaCleanupSelection selection = null);
        Task<MediaCleanupActionResult> Vote(string cleanupRequestId, MediaCleanupVoteType vote);
        Task<MediaCleanupActionResult> Approve(string cleanupRequestId);
        Task<MediaCleanupActionResult> Reject(string cleanupRequestId);
        Task<MediaCleanupActionResult> Cancel(string cleanupRequestId);
        Task ProcessPending();
    }
}
