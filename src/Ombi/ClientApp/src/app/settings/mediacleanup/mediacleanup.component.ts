import { CommonModule } from "@angular/common";
import { Component, OnInit } from "@angular/core";
import { ReactiveFormsModule, UntypedFormBuilder, UntypedFormGroup, Validators } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { MatFormFieldModule } from "@angular/material/form-field";
import { MatInputModule } from "@angular/material/input";
import { MatSelectModule } from "@angular/material/select";
import { MatSlideToggleModule } from "@angular/material/slide-toggle";

import { CommunityCleanupMode, OwnRequestRemovalMode } from "../../interfaces";
import { NotificationService, SettingsService } from "../../services";

@Component({
    standalone: true,
    selector: "app-settings-media-cleanup",
    templateUrl: "./mediacleanup.component.html",
    styleUrls: ["./mediacleanup.component.scss"],
    imports: [
        CommonModule,
        ReactiveFormsModule,
        MatButtonModule,
        MatFormFieldModule,
        MatInputModule,
        MatSelectModule,
        MatSlideToggleModule
    ]
})
export class MediaCleanupSettingsComponent implements OnInit {
    public form!: UntypedFormGroup;
    public readonly OwnRequestRemovalMode = OwnRequestRemovalMode;
    public readonly CommunityCleanupMode = CommunityCleanupMode;

    constructor(
        private readonly settingsService: SettingsService,
        private readonly fb: UntypedFormBuilder,
        private readonly notificationService: NotificationService) { }

    public ngOnInit(): void {
        this.settingsService.getMediaCleanupSettings().subscribe(settings => {
            this.form = this.fb.group({
                id: [settings.id],
                ownRequestRemoval: [settings.ownRequestRemoval],
                communityCleanup: [settings.communityCleanup],
                minimumDeleteVotes: [settings.minimumDeleteVotes, [Validators.required, Validators.min(1)]],
                requiredVoteMargin: [settings.requiredVoteMargin, [Validators.required, Validators.min(0)]],
                votingPeriodDays: [settings.votingPeriodDays, [Validators.required, Validators.min(1)]],
                gracePeriodDays: [settings.gracePeriodDays, [Validators.required, Validators.min(0)]],
                minimumMediaAgeDays: [settings.minimumMediaAgeDays, [Validators.required, Validators.min(0)]],
                requesterCanVeto: [settings.requesterCanVeto],
                deleteFiles: [settings.deleteFiles],
                addImportExclusion: [settings.addImportExclusion]
            });
        });
    }

    public onSubmit(form: UntypedFormGroup): void {
        if (form.invalid) {
            this.notificationService.error("Please check your entered values.");
            return;
        }

        this.settingsService.saveMediaCleanupSettings(form.value).subscribe(result => {
            if (result) {
                this.notificationService.success("Successfully saved Media Cleanup settings.");
            } else {
                this.notificationService.error("There was an error saving Media Cleanup settings.");
            }
        });
    }
}
