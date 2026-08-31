import { ISettings } from "./ICommon";
import { RequestType } from "./IRequestModel";

export enum OwnRequestRemovalMode {
    Off = 0,
    RequestRemoval = 1,
    ImmediateDeletion = 2
}

export enum CommunityCleanupMode {
    Off = 0,
    AdminApproval = 1,
    AutomaticAfterThreshold = 2
}

export enum MediaCleanupStatus {
    Voting = 0,
    PendingAdminApproval = 1,
    ScheduledForDeletion = 2,
    Completed = 3,
    Rejected = 4,
    Failed = 5,
    Cancelled = 6
}

export enum MediaCleanupOrigin {
    OwnRequest = 0,
    Community = 1
}

export enum MediaCleanupVoteType {
    Keep = 0,
    Delete = 1
}

export interface IMediaCleanupSettings extends ISettings {
    ownRequestRemoval: OwnRequestRemovalMode;
    communityCleanup: CommunityCleanupMode;
    minimumDeleteVotes: number;
    requiredVoteMargin: number;
    votingPeriodDays: number;
    gracePeriodDays: number;
    minimumMediaAgeDays: number;
    requesterCanVeto: boolean;
    restrictNominationsToOwnRequests: boolean;
    deleteFiles: boolean;
    addImportExclusion: boolean;
    notifyManagersOnPendingApproval: boolean;
    notifyVotersOnPendingVotes: boolean;
    enabled: boolean;
}

export interface IMediaCleanupOverview {
    settings: IMediaCleanupSettings;
    canRequestRemoval: boolean;
    canDeleteOwnMedia: boolean;
    canVote: boolean;
    canManage: boolean;
    items: IMediaCleanupItem[];
}

export interface IMediaCleanupItem {
    requestType: RequestType;
    requestId: number;
    title: string;
    posterPath: string;
    overview?: string;
    releaseDate?: Date;
    requestedBy: string;
    ownedByCurrentUser: boolean;
    canRequestOwnRemoval: boolean;
    canNominate: boolean;
    canVote: boolean;
    canManage: boolean;
    canCancel: boolean;
    communityAgeEligible: boolean;
    availableSince?: Date;
    lastPlayedAt?: Date;
    lastPlayedKnown: boolean;
    sizeOnDisk: number;
    cleanup?: IMediaCleanupRequest;
}

export interface IMediaCleanupRequest {
    id: string;
    origin: MediaCleanupOrigin;
    status: MediaCleanupStatus;
    keepVotes: number;
    deleteVotes: number;
    requesterVeto: boolean;
    myVote?: MediaCleanupVoteType | null;
    createdAt: Date;
    votingEndsAt?: Date;
    scheduledForDeletionAt?: Date;
    failureReason?: string;
    entireSeries: boolean;
    scopeLabel: string;
    selectedEpisodeCount: number;
    selectedSizeOnDisk: number;
    selectedEpisodes: IMediaCleanupEpisodeSelection[];
    selectedSeasons: number[];
    voters?: IMediaCleanupVoter[];
}

export interface IMediaCleanupSelection {
    entireSeries: boolean;
    episodes: IMediaCleanupEpisodeSelection[];
}

export interface IMediaCleanupEpisodeSelection {
    seasonNumber: number;
    episodeNumber: number;
}

export interface IMediaCleanupTvSelection {
    result: boolean;
    message?: string;
    requestId: number;
    title: string;
    deleteFilesEnabled: boolean;
    sizeOnDisk: number;
    seasons: IMediaCleanupTvSeason[];
}

export interface IMediaCleanupTvSeason {
    seasonNumber: number;
    sizeOnDisk: number;
    episodes: IMediaCleanupTvEpisode[];
}

export interface IMediaCleanupTvEpisode {
    seasonNumber: number;
    episodeNumber: number;
    title: string;
    airDateUtc?: Date;
    hasFile: boolean;
    episodeFileId: number;
    sizeOnDisk: number;
}

export interface IMediaCleanupVoter {
    displayName: string;
    vote: MediaCleanupVoteType;
    date: Date;
    isRequester: boolean;
}

export interface IMediaCleanupActionResult {
    result: boolean;
    message: string;
    cleanupRequestId?: string;
}
