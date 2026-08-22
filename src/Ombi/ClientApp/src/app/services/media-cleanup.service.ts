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
        return this.http.get<IMediaCleanupOverview>(this.url, { headers: this.headers });
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
