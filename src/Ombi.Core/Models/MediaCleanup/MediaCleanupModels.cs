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
        public string Overview { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string RequestedBy { get; set; }
        public bool OwnedByCurrentUser { get; set; }
        public bool IsCleanupSteward { get; set; }
        public DateTime? StewardshipSince { get; set; }
        public bool CanRequestOwnRemoval { get; set; }
        public bool CanNominate { get; set; }
        public bool CanVote { get; set; }
        public bool CanManage { get; set; }
        public bool CanCancel { get; set; }
        public bool CommunityAgeEligible { get; set; }
        public DateTime? AvailableSince { get; set; }
        public DateTime? LastPlayedAt { get; set; }
        public bool LastPlayedKnown { get; set; }
        public long SizeOnDisk { get; set; }
        public MediaCleanupRequestViewModel Cleanup { get; set; }
    }

    public class MediaCleanupSelection
    {
        public bool EntireSeries { get; set; } = true;
        public List<MediaCleanupEpisodeSelection> Episodes { get; set; } = new List<MediaCleanupEpisodeSelection>();
    }

    public class MediaCleanupEpisodeSelection
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
    }

    public class MediaCleanupTvSelectionViewModel
    {
        public bool Result { get; set; }
        public string Message { get; set; }
        public int RequestId { get; set; }
        public string Title { get; set; }
        public bool DeleteFilesEnabled { get; set; }
        public long SizeOnDisk { get; set; }
        public List<MediaCleanupTvSeasonViewModel> Seasons { get; set; } = new List<MediaCleanupTvSeasonViewModel>();
    }

    public class MediaCleanupTvSeasonViewModel
    {
        public int SeasonNumber { get; set; }
        public long SizeOnDisk { get; set; }
        public List<MediaCleanupTvEpisodeViewModel> Episodes { get; set; } = new List<MediaCleanupTvEpisodeViewModel>();
    }

    public class MediaCleanupTvEpisodeViewModel
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; }
        public DateTime? AirDateUtc { get; set; }
        public bool HasFile { get; set; }
        public int EpisodeFileId { get; set; }
        public long SizeOnDisk { get; set; }
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
        public bool EntireSeries { get; set; }
        public string ScopeLabel { get; set; }
        public int SelectedEpisodeCount { get; set; }
        public long SelectedSizeOnDisk { get; set; }
        public List<MediaCleanupEpisodeSelection> SelectedEpisodes { get; set; } = new List<MediaCleanupEpisodeSelection>();
        public List<int> SelectedSeasons { get; set; } = new List<int>();
        public List<MediaCleanupVoterViewModel> Voters { get; set; } = new List<MediaCleanupVoterViewModel>();
    }

    public class MediaCleanupVoterViewModel
    {
        public string DisplayName { get; set; }
        public MediaCleanupVoteType Vote { get; set; }
        public DateTime Date { get; set; }
        public bool IsRequester { get; set; }
    }

    public class MediaCleanupActionResult
    {
        public bool Result { get; set; }
        public string Message { get; set; }
        public string CleanupRequestId { get; set; }
    }
}
