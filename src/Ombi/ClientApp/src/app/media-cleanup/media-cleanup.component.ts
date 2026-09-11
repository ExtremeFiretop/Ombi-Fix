import { CommonModule } from "@angular/common";
import { Component, OnInit } from "@angular/core";
import { MatButtonModule } from "@angular/material/button";
import { MatButtonToggleModule } from "@angular/material/button-toggle";
import { MatCardModule } from "@angular/material/card";
import { MatChipsModule } from "@angular/material/chips";
import { MatIconModule } from "@angular/material/icon";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatDialog, MatDialogModule } from "@angular/material/dialog";
import { MatSelectModule } from "@angular/material/select";
import { MatProgressSpinnerModule } from "@angular/material/progress-spinner";
import { MatTooltipModule } from "@angular/material/tooltip";

import {
    CommunityCleanupMode,
    IMediaCleanupActionResult,
    IMediaCleanupItem,
    IMediaCleanupOverview,
    IMediaCleanupRequest,
    IMediaCleanupVoter,
    MediaCleanupOrigin,
    MediaCleanupStatus,
    MediaCleanupVoteType,
    OwnRequestRemovalMode,
    RequestType
} from "../interfaces";
import { MediaCleanupService, NotificationService } from "../services";
import { Subscription } from "rxjs";
import {
    MediaCleanupTvSelectionDialogResult,
    TvCleanupSelectionDialogComponent
} from "./tv-cleanup-selection-dialog.component";

@Component({
    standalone: true,
    selector: "app-media-cleanup",
    templateUrl: "./media-cleanup.component.html",
    styleUrls: ["./media-cleanup.component.scss"],
    imports: [
        CommonModule,
        MatButtonModule,
        MatButtonToggleModule,
        MatCardModule,
        MatChipsModule,
        MatIconModule,
        MatFormFieldModule,
        MatDialogModule,
        MatSelectModule,
        MatProgressSpinnerModule,
        MatTooltipModule,
        TvCleanupSelectionDialogComponent
    ]
})
export class MediaCleanupComponent implements OnInit {
    public overview!: IMediaCleanupOverview;
    public loading = true;
    public loadingMetrics = false;
    public loadingLastPlayed = false;
    private readonly busyItemKeys = new Set<string>();
    private readonly expandedVoterKeys = new Set<string>();
    private metricsSubscription?: Subscription;
    private lastPlayedSubscription?: Subscription;
    public viewMode: "all" | "mine" | "stewardship" = "mine";
    public mediaTypeMode: "all" | "movie" | "tv" = "all";
    public sortMode: "default" | "size" | "availableSince" | "lastPlayed" | "title" = "default";
    public sortDirection: "asc" | "desc" = "asc";

    public readonly RequestType = RequestType;
    public readonly OwnRequestRemovalMode = OwnRequestRemovalMode;
    public readonly CommunityCleanupMode = CommunityCleanupMode;
    public readonly MediaCleanupStatus = MediaCleanupStatus;
    public readonly MediaCleanupOrigin = MediaCleanupOrigin;
    public readonly MediaCleanupVoteType = MediaCleanupVoteType;

    constructor(
        private readonly cleanupService: MediaCleanupService,
        private readonly notificationService: NotificationService,
        private readonly dialog: MatDialog) { }

    public ngOnInit(): void {
        this.load();
    }

    public load(): void {
        this.loading = true;
        this.cleanupService.getOverview().subscribe({
            next: x => {
                this.overview = x;
                this.ensureValidViewMode();
                this.loading = false;
                this.loadSupplementalData();
            },
            error: () => {
                this.loading = false;
                this.notificationService.error("Unable to load media cleanup.");
            }
        });
    }

    private loadSupplementalData(): void {
        this.loadMetrics();
        this.loadLastPlayed();
    }

    private loadMetrics(): void {
        this.metricsSubscription?.unsubscribe();
        this.loadingMetrics = true;
        this.metricsSubscription = this.cleanupService.getMetricsOverview().subscribe({
            next: metrics => {
                const byRequest = new Map(
                    metrics.items.map(item => [`${item.requestType}-${item.requestId}`, item] as const));
                for (const item of this.overview?.items ?? []) {
                    const metric = byRequest.get(`${item.requestType}-${item.requestId}`);
                    if (metric) {
                        item.sizeOnDisk = metric.sizeOnDisk;
                    }
                }
                this.loadingMetrics = false;
            },
            error: () => {
                // Size is supplemental. Keep the cleanup page usable when an *arr service is slow/unavailable.
                this.loadingMetrics = false;
            }
        });
    }

    private loadLastPlayed(): void {
        this.lastPlayedSubscription?.unsubscribe();
        this.loadingLastPlayed = true;
        this.lastPlayedSubscription = this.cleanupService.getLastPlayedOverview().subscribe({
            next: playback => {
                const byRequest = new Map(
                    playback.items.map(item => [`${item.requestType}-${item.requestId}`, item] as const));
                for (const item of this.overview?.items ?? []) {
                    const played = byRequest.get(`${item.requestType}-${item.requestId}`);
                    if (played) {
                        item.lastPlayedKnown = played.lastPlayedKnown;
                        item.lastPlayedAt = played.lastPlayedAt;
                    }
                }
                this.loadingLastPlayed = false;
            },
            error: () => {
                // Playback history is supplemental. Keep the cleanup page usable when Plex is slow/unavailable.
                this.loadingLastPlayed = false;
            }
        });
    }


    public get activeVoteItems(): IMediaCleanupItem[] {
        if (!this.overview?.canVote) {
            return [];
        }

        // Keep active community cleanup votes visible for the full voting window.
        // Items the current user has not voted on are sorted first, but casting
        // a vote must not remove the nomination from the dashboard.
        return this.overview.items
            .filter(item =>
                item.canVote &&
                item.cleanup?.origin === MediaCleanupOrigin.Community &&
                item.cleanup.status === MediaCleanupStatus.Voting)
            .sort((left, right) => Number(this.hasVoted(left)) - Number(this.hasVoted(right)));
    }

    public hasVoted(item: IMediaCleanupItem): boolean {
        return item.cleanup?.myVote !== undefined && item.cleanup?.myVote !== null;
    }

    public voteText(item: IMediaCleanupItem): string {
        return item.cleanup?.myVote === MediaCleanupVoteType.Keep ? "Keep" : "Remove";
    }

    public cleanupEpisodeSelectionText(cleanup?: IMediaCleanupRequest): string {
        if (!cleanup || cleanup.entireSeries || !cleanup.selectedEpisodes?.length) {
            return "";
        }

        const fullSeasons = new Set(cleanup.selectedSeasons ?? []);
        const partialEpisodes = cleanup.selectedEpisodes
            .filter(x => !fullSeasons.has(x.seasonNumber))
            .sort((a, b) => a.seasonNumber - b.seasonNumber || a.episodeNumber - b.episodeNumber);
        if (!partialEpisodes.length) {
            return "";
        }

        const shown = partialEpisodes.slice(0, 6)
            .map(x => `S${x.seasonNumber.toString().padStart(2, "0")}E${x.episodeNumber.toString().padStart(2, "0")}`);
        const remaining = partialEpisodes.length - shown.length;
        return `Selected episodes: ${shown.join(", ")}${remaining > 0 ? ` +${remaining} more` : ""}`;
    }

    public get pendingVoteCount(): number {
        return this.activeVoteItems.filter(item => !this.hasVoted(item)).length;
    }

    public get castVoteCount(): number {
        return this.activeVoteItems.length - this.pendingVoteCount;
    }

    public get pendingApprovalItems(): IMediaCleanupItem[] {
        if (!this.overview?.canManage) {
            return [];
        }
        return this.overview.items.filter(item => this.canApprove(item));
    }

    public get scheduledRemovalItems(): IMediaCleanupItem[] {
        if (!this.overview?.canManage) {
            return [];
        }

        return this.overview.items
            .filter(item =>
                item.canManage &&
                item.canCancel &&
                item.cleanup?.status === MediaCleanupStatus.ScheduledForDeletion)
            .sort((left, right) => {
                const leftTime = left.cleanup?.scheduledForDeletionAt
                    ? new Date(left.cleanup.scheduledForDeletionAt).getTime()
                    : Number.MAX_SAFE_INTEGER;
                const rightTime = right.cleanup?.scheduledForDeletionAt
                    ? new Date(right.cleanup.scheduledForDeletionAt).getTime()
                    : Number.MAX_SAFE_INTEGER;
                return leftTime - rightTime;
            });
    }

    public get hasDashboardItems(): boolean {
        return this.activeVoteItems.length > 0 ||
            this.pendingApprovalItems.length > 0 ||
            this.scheduledRemovalItems.length > 0;
    }

    public scrollQueue(track: HTMLElement, direction: number): void {
        if (!track) {
            return;
        }
        track.scrollBy({ left: direction * Math.max(320, track.clientWidth * 0.8), behavior: "smooth" });
    }

    public get filteredItems(): IMediaCleanupItem[] {
        let items = this.scopeItems;
        if (this.mediaTypeMode === "movie") {
            items = items.filter(x => x.requestType === RequestType.movie);
        } else if (this.mediaTypeMode === "tv") {
            items = items.filter(x => x.requestType === RequestType.tvShow);
        }

        // Always sort a copy so the overview's original order remains available for
        // the Default option and supplemental background updates do not mutate it.
        return this.sortItems([...items]);
    }

    public get showSortControl(): boolean {
        return this.scopeItems.length > 1;
    }

    public onSortModeChanged(): void {
        // Choose the cleanup-oriented direction whenever the sort field changes.
        switch (this.sortMode) {
            case "size":
                this.sortDirection = "desc";
                break;
            case "availableSince":
            case "lastPlayed":
            case "title":
            default:
                this.sortDirection = "asc";
                break;
        }
    }

    public sortAscendingLabel(): string {
        switch (this.sortMode) {
            case "size": return "Smallest first";
            case "availableSince": return "Oldest first";
            case "lastPlayed": return "Oldest first";
            case "title": return "A–Z";
            default: return "Original order";
        }
    }

    public sortDescendingLabel(): string {
        switch (this.sortMode) {
            case "size": return "Largest first";
            case "availableSince": return "Newest first";
            case "lastPlayed": return "Newest first";
            case "title": return "Z–A";
            default: return "Original order";
        }
    }

    public get sortSupplementalLoading(): boolean {
        return (this.sortMode === "size" && this.loadingMetrics) ||
            (this.sortMode === "lastPlayed" && this.loadingLastPlayed);
    }

    public get sortSupplementalLoadingText(): string {
        return this.sortMode === "size" ? "Loading size data…" : "Loading playback data…";
    }

    public get myMediaCount(): number {
        return this.overview?.items?.filter(x => x.ownedByCurrentUser).length ?? 0;
    }

    public get stewardshipMediaCount(): number {
        return this.overview?.items?.filter(x => x.isCleanupSteward).length ?? 0;
    }

    public get scopedMediaCount(): number {
        return this.scopeItems.length;
    }

    public get movieMediaCount(): number {
        return this.scopeItems.filter(x => x.requestType === RequestType.movie).length;
    }

    public get tvMediaCount(): number {
        return this.scopeItems.filter(x => x.requestType === RequestType.tvShow).length;
    }

    public get showMediaTypeToggle(): boolean {
        const items = this.scopeItems;
        const hasMovies = items.some(x => x.requestType === RequestType.movie);
        const hasTv = items.some(x => x.requestType === RequestType.tvShow);
        return hasMovies && hasTv;
    }

    public get showMyMediaView(): boolean {
        return !!this.overview &&
            this.overview.settings.ownRequestRemoval !== OwnRequestRemovalMode.Off &&
            this.overview.settings.communityCleanup !== CommunityCleanupMode.Off &&
            this.overview.canRequestRemoval &&
            this.overview.canVote;
    }

    public get showStewardshipView(): boolean {
        return this.stewardshipMediaCount > 0;
    }

    public get showViewToggle(): boolean {
        return this.showMyMediaView || this.showStewardshipView;
    }

    public requestOwnRemoval(item: IMediaCleanupItem): void {
        if (item.requestType === RequestType.tvShow) {
            this.openTvSelection(item, "own");
            return;
        }

        const immediate = this.overview.settings.ownRequestRemoval === OwnRequestRemovalMode.ImmediateDeletion;
        if (immediate && !window.confirm(`Permanently remove ${item.title} from the library? Media files will be deleted if that option is enabled.`)) {
            return;
        }
        this.execute(item, this.cleanupService.requestOwnRemoval(item.requestType, item.requestId));
    }

    public nominate(item: IMediaCleanupItem): void {
        if (item.requestType === RequestType.tvShow) {
            this.openTvSelection(item, "community");
            return;
        }
        this.execute(item, this.cleanupService.nominate(item.requestType, item.requestId));
    }

    private openTvSelection(item: IMediaCleanupItem, action: "own" | "community"): void {
        const dialogRef = this.dialog.open(TvCleanupSelectionDialogComponent, {
            width: "900px",
            maxWidth: "96vw",
            data: { item, action },
            panelClass: "modal-panel"
        });

        dialogRef.afterClosed().subscribe((result?: MediaCleanupTvSelectionDialogResult) => {
            if (!result) {
                return;
            }

            if (action === "own") {
                const immediate = this.overview.settings.ownRequestRemoval === OwnRequestRemovalMode.ImmediateDeletion;
                const target = result.selection.entireSeries
                    ? item.title
                    : `${result.summary} of ${item.title}`;
                const warning = result.selection.entireSeries
                    ? `Permanently remove ${target} from the library? Media files will be deleted if Delete Files is enabled.`
                    : `Permanently remove ${target}? The selected Sonarr episode files will be deleted and those episodes will be unmonitored.`;
                if (immediate && !window.confirm(warning)) {
                    return;
                }
                this.execute(
                    item,
                    this.cleanupService.requestOwnRemoval(item.requestType, item.requestId, result.selection),
                    immediate && !result.selection.entireSeries);
                return;
            }

            this.execute(item, this.cleanupService.nominate(item.requestType, item.requestId, result.selection));
        });
    }

    public vote(item: IMediaCleanupItem, vote: MediaCleanupVoteType): void {
        if (!item.cleanup) {
            return;
        }
        this.execute(item, this.cleanupService.vote(item.cleanup.id, vote));
    }

    public approve(item: IMediaCleanupItem): void {
        if (!item.cleanup) {
            return;
        }
        const target = this.cleanupTargetDescription(item);
        if (!window.confirm(`Approve removal of ${target}? It will be deleted after the configured grace period.`)) {
            return;
        }
        this.execute(item, this.cleanupService.approve(item.cleanup.id));
    }

    public reject(item: IMediaCleanupItem): void {
        if (item.cleanup) {
            this.execute(item, this.cleanupService.reject(item.cleanup.id));
        }
    }

    public cancel(item: IMediaCleanupItem): void {
        if (!item.cleanup) {
            return;
        }

        if (item.cleanup.status === MediaCleanupStatus.ScheduledForDeletion &&
            !window.confirm(`Cancel the scheduled removal of ${this.cleanupTargetDescription(item)}? The media will be kept.`)) {
            return;
        }

        this.execute(item, this.cleanupService.cancel(item.cleanup.id));
    }

    public toggleVoters(item: IMediaCleanupItem): void {
        const key = this.itemKey(item);
        if (this.expandedVoterKeys.has(key)) {
            this.expandedVoterKeys.delete(key);
        } else {
            this.expandedVoterKeys.add(key);
        }
    }

    public showVoters(item: IMediaCleanupItem): boolean {
        return this.expandedVoterKeys.has(this.itemKey(item));
    }

    public keepVoters(cleanup: IMediaCleanupRequest): IMediaCleanupVoter[] {
        return (cleanup.voters ?? []).filter(x => x.vote === MediaCleanupVoteType.Keep);
    }

    public removeVoters(cleanup: IMediaCleanupRequest): IMediaCleanupVoter[] {
        return (cleanup.voters ?? []).filter(x => x.vote === MediaCleanupVoteType.Delete);
    }

    public scheduledRemovalCountdown(date?: Date): string {
        if (!date) {
            return "Removal time pending";
        }

        const remainingMs = new Date(date).getTime() - Date.now();
        if (remainingMs <= 0) {
            return "Removal is due";
        }

        const hours = Math.ceil(remainingMs / (60 * 60 * 1000));
        if (hours < 24) {
            return `Deletes in ${hours} hour${hours === 1 ? "" : "s"}`;
        }

        const days = Math.ceil(remainingMs / (24 * 60 * 60 * 1000));
        return `Deletes in ${days} day${days === 1 ? "" : "s"}`;
    }

    public statusText(status: MediaCleanupStatus): string {
        switch (status) {
            case MediaCleanupStatus.Voting: return "Voting";
            case MediaCleanupStatus.PendingAdminApproval: return "Awaiting admin approval";
            case MediaCleanupStatus.ScheduledForDeletion: return "Scheduled for removal";
            case MediaCleanupStatus.Completed: return "Removed";
            case MediaCleanupStatus.Rejected: return "Rejected";
            case MediaCleanupStatus.Failed: return "Failed";
            case MediaCleanupStatus.Cancelled: return "Cancelled";
            default: return "Unknown";
        }
    }

    public ownActionText(): string {
        return this.overview?.settings?.ownRequestRemoval === OwnRequestRemovalMode.ImmediateDeletion
            ? "Remove from library"
            : "Request removal";
    }

    public mediaTypeText(type: RequestType): string {
        return type === RequestType.movie ? "Movie" : type === RequestType.tvShow ? "TV" : "Media";
    }

    public canApprove(item: IMediaCleanupItem): boolean {
        if (!item.cleanup || !item.canManage || item.cleanup.status !== MediaCleanupStatus.PendingAdminApproval) {
            return false;
        }
        return item.cleanup.origin === MediaCleanupOrigin.OwnRequest
            ? this.overview.settings.ownRequestRemoval !== OwnRequestRemovalMode.Off
            : this.overview.settings.communityCleanup !== CommunityCleanupMode.Off;
    }

    public posterUrl(path: string): string {
        if (!path) {
            return "";
        }
        if (path.startsWith("http://") || path.startsWith("https://")) {
            return path;
        }
        return `https://image.tmdb.org/t/p/w342${path.startsWith("/") ? path : `/${path}`}`;
    }

    public formatBytes(bytes: number): string {
        if (!bytes || bytes <= 0) {
            return "Unknown size";
        }
        const units = ["B", "KB", "MB", "GB", "TB"];
        const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
        const value = bytes / Math.pow(1024, index);
        return `${value.toFixed(index >= 3 ? 1 : 0)} ${units[index]}`;
    }


    private get scopeItems(): IMediaCleanupItem[] {
        if (!this.overview) {
            return [];
        }

        if (this.viewMode === "stewardship" && this.showStewardshipView) {
            return this.overview.items.filter(x => x.isCleanupSteward);
        }

        // My Media remains the default where the original ownership view is available.
        // Users whose permissions/settings hide that view must not be silently trapped
        // in a hidden My Media filter.
        if (this.viewMode === "mine" && this.showMyMediaView) {
            return this.overview.items.filter(x => x.ownedByCurrentUser);
        }

        return this.overview.items;
    }

    private ensureValidViewMode(): void {
        if (!this.overview) {
            return;
        }

        if (this.viewMode === "mine" && !this.showMyMediaView) {
            this.viewMode = this.showStewardshipView ? "stewardship" : "all";
            return;
        }

        if (this.viewMode === "stewardship" && !this.showStewardshipView) {
            this.viewMode = this.showMyMediaView ? "mine" : "all";
        }
    }

    private sortItems(items: IMediaCleanupItem[]): IMediaCleanupItem[] {
        if (this.sortMode === "default") {
            return items;
        }

        const direction = this.sortDirection === "asc" ? 1 : -1;
        return items.sort((left, right) => {
            switch (this.sortMode) {
                case "size":
                    return this.compareOptionalNumbers(
                        left.sizeOnDisk > 0 ? left.sizeOnDisk : null,
                        right.sizeOnDisk > 0 ? right.sizeOnDisk : null,
                        direction);

                case "availableSince":
                    return this.compareOptionalNumbers(
                        this.dateValue(left.availableSince),
                        this.dateValue(right.availableSince),
                        direction);

                case "lastPlayed":
                    return this.compareOptionalNumbers(
                        this.lastPlayedSortValue(left),
                        this.lastPlayedSortValue(right),
                        direction);

                case "title":
                    return left.title.localeCompare(right.title, undefined, { sensitivity: "base" }) * direction;

                default:
                    return 0;
            }
        });
    }

    private compareOptionalNumbers(left: number | null, right: number | null, direction: number): number {
        // Unknown values always go last, regardless of ascending/descending direction.
        if (left === null && right === null) {
            return 0;
        }
        if (left === null) {
            return 1;
        }
        if (right === null) {
            return -1;
        }
        return (left - right) * direction;
    }

    private dateValue(value?: Date): number | null {
        if (!value) {
            return null;
        }
        const parsed = new Date(value).getTime();
        return Number.isNaN(parsed) ? null : parsed;
    }

    private lastPlayedSortValue(item: IMediaCleanupItem): number | null {
        if (!item.lastPlayedKnown) {
            return null;
        }

        // Plex explicitly told us there is no play history. Treat Never as older
        // than every real playback date; Unknown remains last via null handling.
        return item.lastPlayedAt ? this.dateValue(item.lastPlayedAt) : Number.NEGATIVE_INFINITY;
    }

    public isItemBusy(item: IMediaCleanupItem): boolean {
        return this.busyItemKeys.has(this.itemKey(item));
    }

    private itemKey(item: IMediaCleanupItem): string {
        return `${item.requestType}-${item.requestId}`;
    }

    private cleanupTargetDescription(item: IMediaCleanupItem): string {
        if (item.requestType !== RequestType.tvShow || !item.cleanup?.scopeLabel || item.cleanup.entireSeries) {
            return item.title;
        }
        return `${item.cleanup.scopeLabel} of ${item.title}`;
    }

    private execute(
        item: IMediaCleanupItem,
        request: import("rxjs").Observable<IMediaCleanupActionResult>,
        refreshMetrics = false): void {
        const key = this.itemKey(item);
        if (this.busyItemKeys.has(key)) {
            return;
        }

        this.busyItemKeys.add(key);
        request.subscribe({
            next: result => {
                if (!result.result) {
                    this.busyItemKeys.delete(key);
                    this.notificationService.error(result.message);
                    return;
                }

                this.notificationService.success(result.message);
                this.refreshItem(item, key, refreshMetrics);
            },
            error: error => {
                this.busyItemKeys.delete(key);
                this.notificationService.error(error?.error?.message ?? "Media cleanup action failed.");
            }
        });
    }

    private refreshItem(item: IMediaCleanupItem, key: string, refreshMetrics = false): void {
        this.cleanupService.getOverviewForRequest(item.requestType, item.requestId).subscribe({
            next: refreshedOverview => {
                const items = [...(this.overview?.items ?? [])];
                const index = items.findIndex(x => x.requestType === item.requestType && x.requestId === item.requestId);
                const refreshed = refreshedOverview.items[0];

                if (index >= 0) {
                    if (refreshed) {
                        // The targeted refresh intentionally skips slow supplemental lookups.
                        // Preserve size/playback data that may already be displayed on the card.
                        refreshed.sizeOnDisk = item.sizeOnDisk;
                        refreshed.lastPlayedAt = item.lastPlayedAt;
                        refreshed.lastPlayedKnown = item.lastPlayedKnown;
                        items[index] = refreshed;
                    } else {
                        // Immediate deletion removes the Ombi request, so it no longer belongs
                        // in the cleanup catalogue.
                        items.splice(index, 1);
                    }

                    this.overview = { ...this.overview, items };
                    this.ensureValidViewMode();
                }

                if (refreshMetrics) {
                    this.loadMetrics();
                }
                this.busyItemKeys.delete(key);
            },
            error: () => {
                this.busyItemKeys.delete(key);
                this.notificationService.warning(
                    "Media Cleanup",
                    "The action completed, but this card could not be refreshed. Refresh the page to update its status.");
            }
        });
    }
}
