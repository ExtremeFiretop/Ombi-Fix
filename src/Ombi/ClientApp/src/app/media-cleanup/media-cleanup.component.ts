import { CommonModule } from "@angular/common";
import { Component, OnInit } from "@angular/core";
import { MatButtonModule } from "@angular/material/button";
import { MatCardModule } from "@angular/material/card";
import { MatChipsModule } from "@angular/material/chips";
import { MatIconModule } from "@angular/material/icon";
import { MatProgressSpinnerModule } from "@angular/material/progress-spinner";
import { MatTooltipModule } from "@angular/material/tooltip";

import {
    CommunityCleanupMode,
    IMediaCleanupActionResult,
    IMediaCleanupItem,
    IMediaCleanupOverview,
    MediaCleanupOrigin,
    MediaCleanupStatus,
    MediaCleanupVoteType,
    OwnRequestRemovalMode,
    RequestType
} from "../interfaces";
import { MediaCleanupService, NotificationService } from "../services";

@Component({
    standalone: true,
    selector: "app-media-cleanup",
    templateUrl: "./media-cleanup.component.html",
    styleUrls: ["./media-cleanup.component.scss"],
    imports: [
        CommonModule,
        MatButtonModule,
        MatCardModule,
        MatChipsModule,
        MatIconModule,
        MatProgressSpinnerModule,
        MatTooltipModule
    ]
})
export class MediaCleanupComponent implements OnInit {
    public overview!: IMediaCleanupOverview;
    public loading = true;
    public busyId?: string;

    public readonly RequestType = RequestType;
    public readonly OwnRequestRemovalMode = OwnRequestRemovalMode;
    public readonly CommunityCleanupMode = CommunityCleanupMode;
    public readonly MediaCleanupStatus = MediaCleanupStatus;
    public readonly MediaCleanupOrigin = MediaCleanupOrigin;
    public readonly MediaCleanupVoteType = MediaCleanupVoteType;

    constructor(
        private readonly cleanupService: MediaCleanupService,
        private readonly notificationService: NotificationService) { }

    public ngOnInit(): void {
        this.load();
    }

    public load(): void {
        this.loading = true;
        this.cleanupService.getOverview().subscribe({
            next: x => {
                this.overview = x;
                this.loading = false;
            },
            error: () => {
                this.loading = false;
                this.notificationService.error("Unable to load media cleanup.");
            }
        });
    }

    public requestOwnRemoval(item: IMediaCleanupItem): void {
        const immediate = this.overview.settings.ownRequestRemoval === OwnRequestRemovalMode.ImmediateDeletion;
        if (immediate && !window.confirm(`Permanently remove ${item.title} from the library? Media files will be deleted if that option is enabled.`)) {
            return;
        }
        this.execute(`own-${item.requestType}-${item.requestId}`,
            this.cleanupService.requestOwnRemoval(item.requestType, item.requestId));
    }

    public nominate(item: IMediaCleanupItem): void {
        this.execute(`nominate-${item.requestType}-${item.requestId}`,
            this.cleanupService.nominate(item.requestType, item.requestId));
    }

    public vote(item: IMediaCleanupItem, vote: MediaCleanupVoteType): void {
        if (!item.cleanup) {
            return;
        }
        this.execute(item.cleanup.id, this.cleanupService.vote(item.cleanup.id, vote));
    }

    public approve(item: IMediaCleanupItem): void {
        if (!item.cleanup) {
            return;
        }
        if (!window.confirm(`Approve removal of ${item.title}? It will be deleted after the configured grace period.`)) {
            return;
        }
        this.execute(item.cleanup.id, this.cleanupService.approve(item.cleanup.id));
    }

    public reject(item: IMediaCleanupItem): void {
        if (item.cleanup) {
            this.execute(item.cleanup.id, this.cleanupService.reject(item.cleanup.id));
        }
    }

    public cancel(item: IMediaCleanupItem): void {
        if (item.cleanup) {
            this.execute(item.cleanup.id, this.cleanupService.cancel(item.cleanup.id));
        }
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

    private execute(id: string, request: import("rxjs").Observable<IMediaCleanupActionResult>): void {
        this.busyId = id;
        request.subscribe({
            next: result => {
                this.busyId = undefined;
                if (result.result) {
                    this.notificationService.success(result.message);
                } else {
                    this.notificationService.error(result.message);
                }
                this.load();
            },
            error: error => {
                this.busyId = undefined;
                this.notificationService.error(error?.error?.message ?? "Media cleanup action failed.");
            }
        });
    }
}
