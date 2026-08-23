import { APP_BASE_HREF } from "@angular/common";
import { HttpClient } from "@angular/common/http";
import { Inject, Injectable } from "@angular/core";
import { Observable } from "rxjs";

import {
    IMediaCleanupActionResult,
    IMediaCleanupOverview,
    MediaCleanupVoteType,
    RequestType
} from "../interfaces";
import { ServiceHelpers } from "./service.helpers";

@Injectable()
export class MediaCleanupService extends ServiceHelpers {
    constructor(public http: HttpClient, @Inject(APP_BASE_HREF) href: string) {
        super(http, "/api/v1/MediaCleanup", href);
    }

    public getOverview(): Observable<IMediaCleanupOverview> {
        // Do not hold the initial page render open on Plex playback-history calls.
        return this.http.get<IMediaCleanupOverview>(`${this.url}?includeMetrics=false&includeLastPlayed=false`, { headers: this.headers });
    }

    public getMetricsOverview(): Observable<IMediaCleanupOverview> {
        // Size information is supplemental and may depend on Radarr/Sonarr availability.
        return this.http.get<IMediaCleanupOverview>(
            `${this.url}?includeMetrics=true&includeLastPlayed=false`,
            { headers: this.headers });
    }

    public getLastPlayedOverview(): Observable<IMediaCleanupOverview> {
        // Playback is supplemental and may depend on Plex availability.
        return this.http.get<IMediaCleanupOverview>(
            `${this.url}?includeMetrics=false&includeLastPlayed=true`,
            { headers: this.headers });
    }

    public getOverviewForRequest(requestType: RequestType, requestId: number): Observable<IMediaCleanupOverview> {
        return this.http.get<IMediaCleanupOverview>(
            `${this.url}?requestType=${requestType}&requestId=${requestId}&includeMetrics=false&includeLastPlayed=false`,
            { headers: this.headers });
    }

    public getOverviewForMedia(requestType: RequestType, mediaId: number): Observable<IMediaCleanupOverview> {
        return this.http.get<IMediaCleanupOverview>(
            `${this.url}?requestType=${requestType}&mediaId=${mediaId}&includeMetrics=false&includeLastPlayed=false`,
            { headers: this.headers });
    }

    public requestOwnRemoval(requestType: RequestType, requestId: number): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/own/${requestType}/${requestId}`, {}, { headers: this.headers });
    }

    public nominate(requestType: RequestType, requestId: number): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/nominate/${requestType}/${requestId}`, {}, { headers: this.headers });
    }

    public vote(cleanupRequestId: string, vote: MediaCleanupVoteType): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/vote/${cleanupRequestId}/${vote}`, {}, { headers: this.headers });
    }

    public approve(cleanupRequestId: string): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/approve/${cleanupRequestId}`, {}, { headers: this.headers });
    }

    public reject(cleanupRequestId: string): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/reject/${cleanupRequestId}`, {}, { headers: this.headers });
    }

    public cancel(cleanupRequestId: string): Observable<IMediaCleanupActionResult> {
        return this.http.post<IMediaCleanupActionResult>(`${this.url}/cancel/${cleanupRequestId}`, {}, { headers: this.headers });
    }
}
