import { CommonModule } from "@angular/common";
import { Component, Inject, OnInit } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from "@angular/material/dialog";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatProgressSpinnerModule } from "@angular/material/progress-spinner";
import { MatSelectModule } from "@angular/material/select";
import { firstValueFrom } from "rxjs";
import { IQualityProfileOption, IQualityProfileRequestDialogResult, RequestType } from "../../interfaces";
import { RadarrService, SonarrService } from "../../services";

export interface QualityProfileRequestDialogData {
    type: RequestType;
    is4K?: boolean;
}

@Component({
    standalone: true,
    selector: "quality-profile-request-dialog",
    imports: [
        CommonModule,
        FormsModule,
        MatButtonModule,
        MatDialogModule,
        MatFormFieldModule,
        MatProgressSpinnerModule,
        MatSelectModule,
    ],
    template: `
        <h2 mat-dialog-title>Quality Profile</h2>
        <mat-dialog-content>
            <p class="quality-profile-help">Choose the profile for this request. For an existing TV series, selecting a profile applies it to the whole series.</p>

            <div class="quality-profile-loading" *ngIf="loading">
                <mat-spinner diameter="32"></mat-spinner>
            </div>

            <mat-form-field appearance="fill" class="quality-profile-field" *ngIf="!loading">
                <mat-label>Profile</mat-label>
                <mat-select [(ngModel)]="selectedProfileId">
                    <mat-option [value]="0">Use existing / configured default</mat-option>
                    <mat-option *ngFor="let profile of profiles" [value]="profile.id">
                        {{ profile.name }}
                    </mat-option>
                </mat-select>
            </mat-form-field>

            <p class="quality-profile-error" *ngIf="errorMessage">{{ errorMessage }}</p>
        </mat-dialog-content>
        <mat-dialog-actions align="end">
            <button mat-button (click)="cancel()">Cancel</button>
            <button mat-raised-button color="primary" [disabled]="loading" (click)="confirm()">Continue</button>
        </mat-dialog-actions>
    `,
    styles: [`
        .quality-profile-help { margin: 0 0 1rem; }
        .quality-profile-field { width: 100%; min-width: 320px; }
        .quality-profile-loading { display: flex; justify-content: center; padding: 1.5rem 0; }
        .quality-profile-error { color: #c62828; margin: .5rem 0 0; }
    `]
})
export class QualityProfileRequestDialogComponent implements OnInit {
    public profiles: IQualityProfileOption[] = [];
    public selectedProfileId = 0;
    public loading = true;
    public errorMessage = "";

    constructor(
        @Inject(MAT_DIALOG_DATA) public data: QualityProfileRequestDialogData,
        private readonly dialogRef: MatDialogRef<QualityProfileRequestDialogComponent, IQualityProfileRequestDialogResult>,
        private readonly radarrService: RadarrService,
        private readonly sonarrService: SonarrService,
    ) {}

    public async ngOnInit(): Promise<void> {
        try {
            this.profiles = this.data.type === RequestType.movie
                ? await firstValueFrom(this.radarrService.getSelectableQualityProfiles(!!this.data.is4K))
                : await firstValueFrom(this.sonarrService.getSelectableQualityProfiles());
        } catch {
            this.errorMessage = "Quality profiles could not be loaded. You can continue without a request-level override.";
            this.profiles = [];
        } finally {
            this.loading = false;
        }
    }

    public cancel(): void {
        this.dialogRef.close();
    }

    public confirm(): void {
        this.dialogRef.close({ profileId: this.selectedProfileId });
    }
}
