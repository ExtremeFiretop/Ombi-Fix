using System;
using System.Collections.Generic;
using Ombi.Settings.Settings.Models;
using Ombi.Store.Entities;

namespace Ombi.Core.Models.MediaCleanup
{
    public class MediaCleanupOverview
    {
        public MediaCleanupSettings Settings { get; set; }
        public bool CanRequestRemoval { get; set; }
        public bool CanDeleteOwnMedia { get; set; }
        public bool CanVote { get; set; }
        public bool CanManage { get; set; }
        public List<MediaCleanupItemViewModel> Items { get; set; } = new List<MediaCleanupItemViewModel>();
    }

    public class MediaCleanupItemViewModel
    {
        public RequestType RequestType { get; set; }
        public int RequestId { get; set; }
        public string Title { get; set; }
        public string PosterPath { get; set; }
        public string RequestedBy { get; set; }
        public bool OwnedByCurrentUser { get; set; }
        public bool CanRequestOwnRemoval { get; set; }
        public bool CanNominate { get; set; }
        public bool CanVote { get; set; }
        public bool CanManage { get; set; }
        public bool CanCancel { get; set; }
        public bool CommunityAgeEligible { get; set; }
        public DateTime? AvailableSince { get; set; }
        public long SizeOnDisk { get; set; }
        public MediaCleanupRequestViewModel Cleanup { get; set; }
    }

    public class MediaCleanupRequestViewModel
    {
        public string Id { get; set; }
        public MediaCleanupOrigin Origin { get; set; }
        public MediaCleanupStatus Status { get; set; }
        public int KeepVotes { get; set; }
        public int DeleteVotes { get; set; }
        public bool RequesterVeto { get; set; }
        public MediaCleanupVoteType? MyVote { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? VotingEndsAt { get; set; }
        public DateTime? ScheduledForDeletionAt { get; set; }
        public string FailureReason { get; set; }
    }

    public class MediaCleanupActionResult
    {
        public bool Result { get; set; }
        public string Message { get; set; }
        public string CleanupRequestId { get; set; }
    }
}
