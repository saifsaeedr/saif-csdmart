import {
    type ActionRequest,
    type ActionResponse,
    type ApiQueryResponse,
    ContentType,
    Dmart,
    type QueryRequest,
    QueryType,
    DmartScope,
    RequestType,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { user, getCurrentScope } from "@/stores/user";
import { get } from "svelte/store";
import { getFileType } from "../helpers";
import { getSpaces } from "./spaces";
import { log } from "@/lib/logger";
import { MESSAGES_SPACE } from "@/lib/constants";
import { ensureUploadSize } from "./core";

export type StreamEntitiesOptions = {
    // Restrict the fan-out to a single space (matches the "Current space:" tag chip).
    spaceFilter?: string;
    // Restrict the query to an exact subpath within each space (matches the
    // "Current folder:" tag chip). Falsy = whole space.
    subpathFilter?: string;
    // Per-space row cap. Defaults to 50 to keep the dropdown DOM bounded
    // when a space contains many entries.
    limitPerSpace?: number;
    // Cap on simultaneous in-flight per-space queries. Without this, every
    // keystroke against a tenant with many spaces fires N concurrent
    // requests; 6 keeps both client connection budget and server load
    // bounded while still feeling instant on typical setups.
    maxConcurrent?: number;
};

/**
 * Fans a search out across spaces, invoking `onBatch` as each space's query
 * resolves so callers can render results progressively. Returns a `cancel`
 * handle so a newer search can ignore stale in-flight batches.
 *
 * Per-space queries are isolated: if one space's query rejects, the failure
 * is logged and the rest of the fan-out continues. The aggregate `done`
 * promise still resolves so callers can flip their loading state.
 *
 * Concurrency is capped at `maxConcurrent` (default 6). Queries beyond the
 * cap queue and start as earlier ones resolve, so a tenant with 50 spaces
 * doesn't open 50 simultaneous HTTP connections per keystroke.
 */
export function streamEntitiesAcrossSpaces(
    search: string,
    onBatch: (records: any[], space: string) => void,
    options: StreamEntitiesOptions = {}
): { done: Promise<void>; cancel: () => void } {
    let cancelled = false;
    const limit = options.limitPerSpace ?? 50;
    const maxConcurrent = Math.max(1, options.maxConcurrent ?? 6);

    const done = (async () => {
        let spaces: string[];
        if (options.spaceFilter) {
            spaces = [options.spaceFilter];
        } else {
            const result = await getSpaces();
            if (cancelled) return;
            spaces = result.records.map((space) => space.shortname);
        }

        const subpath = options.subpathFilter
            ? options.subpathFilter.startsWith("/")
                ? options.subpathFilter
                : `/${options.subpathFilter}`
            : "/";
        const exactSubpath = !!options.subpathFilter;

        // Pull-based concurrency limiter: spawn `maxConcurrent` workers,
        // each draining the same `cursor` index so no global queue
        // bookkeeping is needed. When all workers exit, fan-out is done.
        let cursor = 0;
        const querySpace = async (space: string) => {
            if (cancelled) return;
            const queryRequest: QueryRequest = {
                filter_shortnames: [],
                type: QueryType.subpath,
                space_name: space,
                subpath,
                exact_subpath: exactSubpath,
                sort_by: "shortname",
                sort_type: SortType.ascending,
                search,
                limit,
                retrieve_json_payload: true,
                retrieve_attachments: true,
            };
            try {
                const response: ApiQueryResponse = (await Dmart.query(queryRequest))!;
                if (cancelled) return;
                onBatch(response?.records ?? [], space);
            } catch (error) {
                if (cancelled) return;
                log.error(`Search failed for space "${space}":`, error);
                onBatch([], space);
            }
        };
        const worker = async () => {
            while (!cancelled) {
                const i = cursor++;
                if (i >= spaces.length) return;
                await querySpace(spaces[i]);
            }
        };
        const workerCount = Math.min(maxConcurrent, spaces.length);
        await Promise.all(Array.from({ length: workerCount }, () => worker()));
    })();

    return {
        done,
        cancel: () => {
            cancelled = true;
        },
    };
}

export async function getMyEntities(shortname: string = "") {
    const result = await getSpaces(false, DmartScope.managed, [
        MESSAGES_SPACE,
        "poll",
        "surveys",
    ]);
    const spaces = result.records.map((space) => space.shortname);

    const promises = spaces.map(async (space) => {
        let currentUser = get(user);
        const search = `@owner_shortname:${shortname || currentUser.shortname}`;

        const queryRequest: QueryRequest = {
            filter_shortnames: [],
            type: QueryType.subpath,
            space_name: space,
            subpath: "/",
            exact_subpath: false,
            sort_by: "created_at",
            sort_type: SortType.ascending,
            search,
            retrieve_json_payload: true,
            retrieve_attachments: true,
        };

        const response: ApiQueryResponse = (await Dmart.query(queryRequest))!;
        return response?.records ?? [];
    });

    const allRecordsArrays = await Promise.all(promises);

    return allRecordsArrays.flat();
}



export async function getEntityAttachmentsCount(
    shortname: string,
    spaceName: string,
    subpath: string
) {
    let cleanSubpath = subpath ? (subpath.startsWith("/") ? subpath.substring(1) : subpath) : "";
    if (cleanSubpath === "__root__") cleanSubpath = "";
    const targetSubpath = cleanSubpath ? `${cleanSubpath}/${shortname}` : shortname;

    const query: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.attachments_aggregation,
        space_name: spaceName,
        subpath: targetSubpath,
        limit: 100,
        sort_by: "shortname",
        sort_type: SortType.ascending,
        offset: 0,
        search: "",
        retrieve_json_payload: true,
        retrieve_attachments: true,
    };
    const response = await Dmart.query(query, getCurrentScope());

    return response?.records ?? [];
}

export async function attachAttachmentsToEntity(
    shortname: string,
    spaceName: string,
    subpath: string,
    attachment: File,
    metadata?: {
        shortname?: string;
        displayname?: Record<string, string>;
        description?: Record<string, string>;
    }
) {
    ensureUploadSize(attachment);
    const fileType = getFileType(attachment);
    const resourceType = fileType ? fileType.resourceType : ResourceType.media;

    let cleanSubpath = subpath ? (subpath.startsWith("/") ? subpath.substring(1) : subpath) : "";
    if (cleanSubpath === "__root__") cleanSubpath = "";
    const targetSubpath = cleanSubpath ? `${cleanSubpath}/${shortname}` : shortname;

    const trimmedShortname = metadata?.shortname?.trim();
    const displaynamePayload = metadata?.displayname && Object.keys(metadata.displayname).length > 0
        ? metadata.displayname
        : undefined;
    const descriptionPayload = metadata?.description && Object.keys(metadata.description).length > 0
        ? metadata.description
        : undefined;

    const attributes: Record<string, any> = { is_active: true };
    if (displaynamePayload) attributes.displayname = displaynamePayload;
    if (descriptionPayload) attributes.description = descriptionPayload;

    const response = await Dmart.uploadWithPayload({
        space_name: spaceName,
        subpath: targetSubpath,
        shortname: trimmedShortname || "auto",
        resource_type: resourceType,
        payload_file: attachment,
        attributes: Object.keys(attributes).length > 1 ? attributes : undefined,
    });
    return response.status === "success" && response.records.length > 0;
}

export async function searchInCatalog(search: string = "", limit: number = 20, offset: number = 0) {
    const result = await getSpaces(false, getCurrentScope());
    const spaces = result.records.map((space) => space.shortname);

    const promises = spaces.map(async (space) => {
        const queryRequest: QueryRequest = {
            filter_shortnames: [],
            type: QueryType.subpath,
            space_name: space,
            subpath: "/",
            exact_subpath: false,
            sort_by: "created_at",
            sort_type: SortType.ascending,
            search,
            limit,
            offset,
            retrieve_json_payload: true,
            retrieve_attachments: false,
        };

        const response: ApiQueryResponse = (await Dmart.query(
            queryRequest,
            getCurrentScope()
        ))!;
        return response?.records ?? [];
    });

    const allRecordsArrays = await Promise.all(promises);

    return allRecordsArrays.flat();
}

export async function createFolder(
    spaceName: string,
    subpath: string,
    data: any
) {
    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
            {
                resource_type: ResourceType.folder,
                shortname: data.shortname || "auto",
                subpath: subpath.startsWith("/") ? subpath : `/${subpath}`,
                attributes: {
                    displayname: data.displayname || {},
                    description: data.description || {},
                    is_active: data.is_active !== undefined ? data.is_active : true,
                    payload: {
                        body: data.folderContent || {},
                        content_type: ContentType.json,
                    },
                },
            },
        ],
    };

    try {
        const response: ActionResponse = await Dmart.request(actionRequest);
        if (response.status === "success" && response.records.length > 0) {
            return response.records[0].shortname;
        }
        return null;
    } catch (error) {
        log.error(`Error creating folder in ${spaceName}/${subpath}:`, error);
        throw error;
    }
}

