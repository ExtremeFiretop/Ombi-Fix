import { CommonModule } from "@angular/common";
import { Component, Inject, OnInit } from "@angular/core";
import { SelectionModel } from "@angular/cdk/collections";
import { MatButtonModule } from "@angular/material/button";
import { MatButtonToggleModule } from "@angular/material/button-toggle";
import { MatCheckboxModule } from "@angular/material/checkbox";
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatExpansionModule } from "@angular/material/expansion";
import { MatIconModule } from "@angular/material/icon";
import { MatProgressSpinnerModule } from "@angular/material/progress-spinner";

import {
    IMediaCleanupItem,
    IMediaCleanupSelection,
    IMediaCleanupTvEpisode,
    IMediaCleanupTvSeason
} from "../interfaces";
import { MediaCleanupService } from "../services";

export interface MediaCleanupTvSelectionDialogData {
    item: IMediaCleanupItem;
    action: "own" | "community";
}

export interface MediaCleanupTvSelectionDialogResult {
    selection: IMediaCleanupSelection;
    summary: string;
    selectedSizeOnDisk: number;
}

@Component({
    standalone: true,
    selector: "app-tv-cleanup-selection-dialog",
    templateUrl: "./tv-cleanup-selection-dialog.component.html",
    styleUrls: ["./tv-cleanup-selection-dialog.component.scss"],
    imports: [
        CommonModule,
        MatButtonModule,
        MatButtonToggleModule,
        MatCheckboxModule,
        MatDialogModule,
        MatExpansionModule,
        MatIconModule,
        MatProgressSpinnerModule
    ]
})
export class TvCleanupSelectionDialogComponent implements OnInit {
    public loading = true;
    public errorMessage = "";
    public episodeLoadError = "";
    public mode: "entire" | "episodes" = "entire";
    public seasons: IMediaCleanupTvSeason[] = [];
    public seriesSize = 0;
    public deleteFilesEnabled = true;
    public readonly selection = new SelectionModel<IMediaCleanupTvEpisode>(true, []);

    constructor(
        public readonly dialogRef: MatDialogRef<TvCleanupSelectionDialogComponent>,
        @Inject(MAT_DIALOG_DATA) public readonly data: MediaCleanupTvSelectionDialogData,
        private readonly cleanupService: MediaCleanupService) { }

    public ngOnInit(): void {
        this.cleanupService.getTvSelection(this.data.item.requestId).subscribe({
            next: result => {
                this.loading = false;
                this.deleteFilesEnabled = result.deleteFilesEnabled;
                if (!result.result) {
                    this.episodeLoadError = result.message ?? "Unable to load episodes from Sonarr.";
                    return;
                }
                this.seasons = result.seasons ?? [];
                this.seriesSize = result.sizeOnDisk ?? 0;
            },
            error: error => {
                this.loading = false;
                this.episodeLoadError = error?.error?.message ?? "Unable to load episodes from Sonarr.";
            }
        });
    }

    public seasonLabel(seasonNumber: number): string {
        return seasonNumber === 0 ? "Specials" : `Season ${seasonNumber}`;
    }

    public fileEpisodes(season: IMediaCleanupTvSeason): IMediaCleanupTvEpisode[] {
        return season.episodes.filter(x => x.hasFile && x.episodeFileId > 0);
    }

    public isEpisodeSelected(episode: IMediaCleanupTvEpisode): boolean {
        if (!episode.hasFile || episode.episodeFileId <= 0) {
            return false;
        }
        return this.linkedEpisodes(episode.episodeFileId).every(x => this.selection.isSelected(x));
    }

    public toggleEpisode(episode: IMediaCleanupTvEpisode): void {
        if (!episode.hasFile || episode.episodeFileId <= 0) {
            return;
        }

        const linked = this.linkedEpisodes(episode.episodeFileId);
        if (linked.every(x => this.selection.isSelected(x))) {
            linked.forEach(x => this.selection.deselect(x));
        } else {
            linked.forEach(x => this.selection.select(x));
        }
    }

    public isAllSeasonSelected(season: IMediaCleanupTvSeason): boolean {
        const episodes = this.fileEpisodes(season);
        return episodes.length > 0 && episodes.every(x => this.isEpisodeSelected(x));
    }

    public isSomeSeasonSelected(season: IMediaCleanupTvSeason): boolean {
        const episodes = this.fileEpisodes(season);
        return episodes.some(x => this.isEpisodeSelected(x)) && !this.isAllSeasonSelected(season);
    }

    public toggleSeason(season: IMediaCleanupTvSeason): void {
        const episodes = this.fileEpisodes(season);
        if (this.isAllSeasonSelected(season)) {
            episodes.forEach(x => this.linkedEpisodes(x.episodeFileId).forEach(linked => this.selection.deselect(linked)));
        } else {
            episodes.forEach(x => this.linkedEpisodes(x.episodeFileId).forEach(linked => this.selection.select(linked)));
        }
    }

    public get canChooseEpisodes(): boolean {
        return this.deleteFilesEnabled && !this.episodeLoadError && this.selectableEpisodeCount > 0;
    }

    public get selectedEpisodeCount(): number {
        return this.uniqueSelectedEpisodes.length;
    }

    public get selectedSizeOnDisk(): number {
        const files = new Map<number, number>();
        for (const episode of this.uniqueSelectedEpisodes) {
            if (!files.has(episode.episodeFileId)) {
                files.set(episode.episodeFileId, episode.sizeOnDisk ?? 0);
            }
        }
        return Array.from(files.values()).reduce((sum, size) => sum + size, 0);
    }

    public get selectableEpisodeCount(): number {
        return this.seasons.reduce((count, season) => count + this.fileEpisodes(season).length, 0);
    }

    public submit(): void {
        this.errorMessage = "";
        if (this.mode === "entire") {
            this.dialogRef.close(<MediaCleanupTvSelectionDialogResult>{
                selection: { entireSeries: true, episodes: [] },
                summary: "Entire series",
                selectedSizeOnDisk: this.seriesSize
            });
            return;
        }

        if (!this.deleteFilesEnabled) {
            this.errorMessage = "Specific episode cleanup requires Delete Files to be enabled in Media Cleanup settings.";
            return;
        }

        const episodes = this.uniqueSelectedEpisodes;
        if (episodes.length === 0) {
            this.errorMessage = "Select at least one episode with a file.";
            return;
        }

        this.dialogRef.close(<MediaCleanupTvSelectionDialogResult>{
            selection: {
                entireSeries: false,
                episodes: episodes.map(x => ({ seasonNumber: x.seasonNumber, episodeNumber: x.episodeNumber }))
            },
            summary: this.selectionSummary(episodes),
            selectedSizeOnDisk: this.selectedSizeOnDisk
        });
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

    private get uniqueSelectedEpisodes(): IMediaCleanupTvEpisode[] {
        const result = new Map<string, IMediaCleanupTvEpisode>();
        for (const episode of this.selection.selected) {
            result.set(`${episode.seasonNumber}-${episode.episodeNumber}`, episode);
        }
        return Array.from(result.values())
            .sort((a, b) => a.seasonNumber - b.seasonNumber || a.episodeNumber - b.episodeNumber);
    }

    private linkedEpisodes(episodeFileId: number): IMediaCleanupTvEpisode[] {
        if (episodeFileId <= 0) {
            return [];
        }
        return this.seasons
            .flatMap(season => season.episodes)
            .filter(x => x.hasFile && x.episodeFileId === episodeFileId);
    }

    private selectionSummary(episodes: IMediaCleanupTvEpisode[]): string {
        const seasons = Array.from(new Set(episodes.map(x => x.seasonNumber))).sort((a, b) => a - b);
        const wholeSeasons = seasons.filter(seasonNumber => {
            const season = this.seasons.find(x => x.seasonNumber === seasonNumber);
            return !!season && this.isAllSeasonSelected(season);
        });

        if (wholeSeasons.length === seasons.length) {
            if (seasons.length === 1) {
                return this.seasonLabel(seasons[0]);
            }
            return `Seasons ${seasons.map(x => x === 0 ? "Specials" : x).join(", ")}`;
        }

        if (seasons.length === 1) {
            return `${episodes.length} episode${episodes.length === 1 ? "" : "s"} from ${this.seasonLabel(seasons[0])}`;
        }
        return `${episodes.length} episode${episodes.length === 1 ? "" : "s"} across ${seasons.length} seasons`;
    }
}
