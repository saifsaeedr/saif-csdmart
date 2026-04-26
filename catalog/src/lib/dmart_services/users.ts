import {
    type ActionRequest,
    type ActionResponse,
    type ApiQueryResponse,
    ContentType,
    Dmart,
    type QueryRequest,
    QueryType,
    RequestType,
    DmartScope,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { getEntity, createEntity, updateEntity } from "./core";
import { log } from "@/lib/logger";
import { getCurrentScope } from "@/stores/user";
import { MANAGEMENT_SPACE, APPLICATIONS_SPACE } from "@/lib/constants";
import { website } from "@/config";

export async function getAllUsers(
    limit: number = 100,
    offset: number = 0,
    search: string = ""
): Promise<ApiQueryResponse> {
    try {
        const searchQuery = search.trim()
            ? `@resource_type:user ${search.trim()}`
            : "@resource_type:user";
        return (await Dmart.query({
            type: QueryType.search,
            space_name: MANAGEMENT_SPACE,
            subpath: "users",
            search: searchQuery,
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            exact_subpath: false,
        }))!;
    } catch (error: any) {
        if (error?.response?.status !== 401 && error?.status !== 401) {
            log.error("Error fetching users:", error);
        }
        return { status: "failed", records: [], attributes: { total: 0, returned: 0 } } as ApiQueryResponse;
    }
}

export async function filterUserByRole(
    role: string,
    limit: number = 100,
    offset: number = 0,
    search: string = ""
): Promise<ApiQueryResponse> {
    try {
        const searchQuery = search.trim()
            ? `@resource_type:user @roles:${role} ${search.trim()}`
            : `@resource_type:user @roles:${role}`;
        return (await Dmart.query({
            type: QueryType.search,
            space_name: MANAGEMENT_SPACE,
            subpath: "users",
            search: searchQuery,
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            exact_subpath: false,
        }))!;
    } catch (error) {
        log.error("Error filtering users by role:", error);
        return { status: "failed", records: [], attributes: { total: 0, returned: 0 } } as ApiQueryResponse;
    }
}

export async function updateUserRoles(
    userShortname: string,
    roles: string[]
): Promise<boolean> {
    try {
        const actionRequest: ActionRequest = {
            space_name: MANAGEMENT_SPACE,
            request_type: RequestType.update,
            records: [
                {
                    resource_type: ResourceType.user,
                    shortname: userShortname,
                    subpath: `users/${userShortname}`,
                    attributes: {
                        roles: roles,
                    },
                },
            ],
        };

        const response: ActionResponse = await Dmart.request(actionRequest);
        return response.status === "success";
    } catch (error) {
        log.error("Error updating user roles:", error);
        return false;
    }
}

export async function getUsersByShortnames(
    shortnames: string[]
): Promise<ApiQueryResponse> {
    try {
        if (shortnames.length === 0) {
            return (await Dmart.query(
                {
                    type: QueryType.search,
                    space_name: MANAGEMENT_SPACE,
                    subpath: "users",
                    limit: 0,
                    sort_by: "shortname",
                    sort_type: SortType.ascending,
                    offset: 0,
                    search: "",
                    retrieve_json_payload: true,
                    retrieve_attachments: false,
                    exact_subpath: true,
                },
                DmartScope.managed
            ))!;
        }

        const query: QueryRequest = {
            filter_shortnames: shortnames,
            type: QueryType.search,
            space_name: MANAGEMENT_SPACE,
            subpath: "users",
            limit: shortnames.length,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: 0,
            search: "",
            retrieve_json_payload: true,
            retrieve_attachments: false,
            exact_subpath: true,
        };

        return (await Dmart.query(query, DmartScope.managed))!;
    } catch (error) {
        log.error("Error fetching users by shortnames:", error);
        return (await Dmart.query(
            {
                type: QueryType.search,
                space_name: MANAGEMENT_SPACE,
                subpath: "users",
                limit: 0,
                sort_by: "shortname",
                sort_type: SortType.ascending,
                offset: 0,
                search: "",
                retrieve_json_payload: true,
                retrieve_attachments: false,
                exact_subpath: true,
            },
            DmartScope.managed
        ))!;
    }
}

export async function setDefaultUserRole(
    roleShortname: string
): Promise<boolean> {
    try {
        const existingConfig = await getEntity(
            "web_config",
            APPLICATIONS_SPACE,
            DmartScope.public,
            ResourceType.content,
            DmartScope.managed,
            true,
            false
        );

        if (existingConfig) {
            const payload = existingConfig.payload?.body;
            if (!payload) return false;
            let updatedItems = payload.items || [];

            const existingItemIndex = updatedItems.findIndex(
                (item: any) => item.key === "default_user_role"
            );

            if (existingItemIndex !== -1) {
                updatedItems[existingItemIndex] = {
                    key: "default_user_role",
                    value: roleShortname,
                };
            } else {
                updatedItems.push({
                    key: "default_user_role",
                    value: roleShortname,
                });
            }

            const result = await updateEntity(
                "web_config",
                APPLICATIONS_SPACE,
                DmartScope.public,
                ResourceType.content,
                {
                    payload: {
                        content_type: ContentType.json,
                        body: {
                            items: updatedItems,
                        },
                    },
                },
            );

            return result !== null;
        } else {
            const attributes: any = {
                displayname: { en: "Default User Role Configuration" },
                description: { en: `Default role assigned to new users: ${roleShortname}`, ar: "", ku: "" },
                is_active: true,
                tags: ["config", "user_role"],
                relationships: [],
                payload: {
                    content_type: ContentType.json,
                    body: {
                        items: [{
                            key: "default_user_role",
                            value: roleShortname
                        }]
                    }
                }
            };

            const result = await createEntity(
                APPLICATIONS_SPACE,
                "configs",
                ResourceType.content,
                attributes,
                "web_config"
            );
            return result !== null;
        }
    } catch (error) {
        log.error("Error setting default user role:", error);
        return false;
    }
}

/**
 * Fetch the set of currently online user shortnames from the
 * csdmart /ws-info REST endpoint.
 * Uses direct fetch (not Dmart axios) to avoid interceptor issues.
 */
export async function fetchOnlineUsers(): Promise<Set<string>> {
    try {
        const baseUrl = website.backend.replace(/\/+$/, "");
        const token = localStorage.getItem("authToken");
        const headers: Record<string, string> = {};
        if (token) {
            headers["Authorization"] = `Bearer ${token}`;
        }

        const res = await fetch(`${baseUrl}/ws-info`, { headers });

        if (!res.ok) {
            console.error("[fetchOnlineUsers] HTTP", res.status, res.statusText);
            return new Set();
        }

        const json = await res.json();
        console.log("[fetchOnlineUsers] raw response:", JSON.stringify(json));

        // csdmart shape: { status, data: { connected_clients, channels: { ch: [users] } } }
        const channels = json?.data?.channels;
        if (!channels || typeof channels !== "object") {
            return new Set();
        }

        const onlineUsers = new Set<string>();
        for (const subscribers of Object.values(channels)) {
            if (Array.isArray(subscribers)) {
                for (const u of subscribers) {
                    onlineUsers.add(u as string);
                }
            }
        }
        console.log("[fetchOnlineUsers] online:", [...onlineUsers]);
        return onlineUsers;
    } catch (error) {
        console.error("[fetchOnlineUsers] error:", error);
        return new Set();
    }
}
