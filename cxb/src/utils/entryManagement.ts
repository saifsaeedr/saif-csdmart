import { Dmart, RequestType, ResourceType, type ResponseEntry, type ActionRequest } from "@edraj/tsdmart";
import { removeEmpty } from "@/utils/renderer/schemaEntryRenderer";
import { Level, showToast } from "@/utils/toast";
import { jsonEditorContentParser } from "@/utils/jsonEditor";

/**
 * Gets the parent subpath from a given path
 */
export function getParentSubpath(path: string): string {
    const normalizedPath = path.replace(/^\/+|\/+$/g, "");
    const parts = normalizedPath.split("/");

    if (parts.length <= 1 || (parts.length === 1 && parts[0] === "")) {
        return "/";
    }

    return "/" + parts.slice(0, -1).join("/");
}

/**
 * Recursively drops props whose value is an empty string now AND was an empty
 * string (or absent) before. Keeps a field that was cleared from a real value
 * to "" (originalValue is non-empty) — that's a genuine change. Arrays, nested
 * non-object values, null, and non-empty values are left untouched. Mutates
 * `current` in place.
 */
function stripUnchangedEmptyStrings(current: any, original: any): void {
    if (!current || typeof current !== "object" || Array.isArray(current)) return;
    const orig = (original && typeof original === "object" && !Array.isArray(original)) ? original : {};
    for (const key of Object.keys(current)) {
        const value = current[key];
        if (value === "" && (orig[key] === "" || orig[key] === undefined)) {
            delete current[key];
        } else if (value && typeof value === "object" && !Array.isArray(value)) {
            stripUnchangedEmptyStrings(value, orig[key]);
            // A nested object the strip emptied entirely (every prop was an
            // unchanged empty) and that the original never carried is a spurious
            // {} — drop it too, so editing a never-filled nested group doesn't
            // write an empty object. A genuine change keeps at least one prop,
            // so this only fires on no-op subtrees.
            if (orig[key] === undefined && Object.keys(value).length === 0) {
                delete current[key];
            }
        }
    }
}

/**
 * Handles saving entry data with proper content processing
 */
export async function saveEntry(
    jeContent: any,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    originalJeContent?: any
): Promise<{ success: boolean; errorMessage?: string }> {
    let content;
    try {
        content = jsonEditorContentParser(jeContent);
    } catch (error) {
        return { success: false, errorMessage: "Invalid JSON format" };
    }

    const shortname = content.shortname;
    delete content.uuid;
    delete content.shortname;

    if (resource_type === ResourceType.schema) {
        content.payload.body = removeEmpty(content.payload.body);
    } else if (resource_type === ResourceType.content && subpath === "workflows") {
        content.payload = {
            body: removeEmpty(jsonEditorContentParser(content.payload.body)),
            schema: 'workflow',
            content_type: "json"
        };
    }

    if (resource_type === ResourceType.user) {
        // Admin UI does not set passwords — /managed/request rejects them. Strip
        // any password/old_password (a loaded $argon2id hash or a stale field) so
        // the update never carries one; users set their own via OTP / reset.
        delete content.password;
        delete content.old_password;
    }

    if (originalJeContent) {
        const originalContent = jsonEditorContentParser(originalJeContent);
        if (originalJeContent?.payload?.content_type === 'json') {
            if (originalContent.payload && originalContent.payload.body && content.payload && content.payload.body) {
                const originalKeys = Object.keys(originalContent.payload.body);
                const currentKeys = Object.keys(content.payload.body);
                const removedKeys = originalKeys.filter(key => !currentKeys.includes(key));
                removedKeys.forEach(key => {
                    content.payload.body[key] = null;
                });
            }
        }
        // Don't send props that are an empty string now and were empty/absent
        // before — editing must not write spurious "" for never-filled or
        // already-empty fields. (A field cleared from a real value to "" is
        // kept by the helper, since that's a genuine change.)
        stripUnchangedEmptyStrings(content, originalContent);
    }
    let _subpath = resource_type === ResourceType.folder ? getParentSubpath(subpath) : subpath
    _subpath = _subpath.replaceAll('-', '/')
    try {
        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.update,
            records: [{
                resource_type: resource_type,
                shortname: shortname,
                subpath: _subpath,
                attributes: content as Record<string, any>
            }]
        });
        showToast(Level.info, `Entry has been updated successfully!`);
        return { success: true };
    } catch (error: any) {
        return { success: false, errorMessage: error.response?.data || error.message };
    }
}

/**
 * Deletes an entry
 */
export async function deleteEntry(
    entry: ResponseEntry,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    force: boolean = false
): Promise<{ success: boolean; errorMessage?: string }> {
    let targetSubpath: string;
    if (resource_type === ResourceType.folder) {
        const arr = subpath.split("/");
        arr[arr.length - 1] = "";
        targetSubpath = arr.join("/");
    } else {
        targetSubpath = subpath;
    }

    try {
        const body: ActionRequest & { force?: boolean } = {
            space_name: space_name,
            request_type: RequestType.delete,
            force,
            records: [{
                resource_type: resource_type,
                shortname: entry.shortname,
                subpath: targetSubpath || '/',
                attributes: {}
            }]
        };
        await Dmart.request(body);
        showToast(Level.info, `Entry deleted successfully`);
        return { success: true };
    } catch (error: any) {
        showToast(Level.warn, `Failed to delete the entry!`);
        return { success: false, errorMessage: error.response?.data?.error };
    }
}

/**
 * Moves an entry to trash
 */
export async function moveEntryToTrash(
    entry: ResponseEntry,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    userShortname: string
): Promise<{ success: boolean; errorMessage?: string }> {
    try {
        const moveResourceType = resource_type;
        const moveNewSubpath = moveResourceType === ResourceType.folder
            ? (subpath.split("/").slice(0, -1).join("-") || '/')
            : subpath;

        const moveAttrb = {
            src_space_name: space_name,
            src_subpath: moveNewSubpath,
            src_shortname: entry.shortname,
            dest_space_name: 'personal',
            dest_subpath: `/people/${userShortname}/trash/${space_name}/${moveNewSubpath}`.replaceAll('//', '/'),
            dest_shortname: entry.shortname,
        };

        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.move,
            records: [
                {
                    resource_type: moveResourceType,
                    shortname: entry.shortname,
                    subpath: moveNewSubpath,
                    attributes: moveAttrb,
                },
            ],
        });
        showToast(Level.info, `Entry deleted successfully`);
        return { success: true };
    } catch (error: any) {
        showToast(Level.warn, `Failed to delete the entry!`);
        return { success: false, errorMessage: error.message };
    }
}

/**
 * Gets payload schema for a given schema shortname
 */
export async function getPayloadSchema(schemaShortname: string, space_name: string): Promise<any> {
    if (schemaShortname === "folder_rendering") {
        return await Dmart.retrieveEntry({ resource_type: ResourceType.schema, space_name: "management", subpath: "schema", shortname: schemaShortname, retrieve_json_payload: true, retrieve_attachments: false, validate_schema: true });
    }
    return await Dmart.retrieveEntry({ resource_type: ResourceType.schema, space_name, subpath: "schema", shortname: schemaShortname, retrieve_json_payload: true, retrieve_attachments: false, validate_schema: true });
}

/**
 * Moves multiple entries to trash
 */
export async function bulkMoveEntryToTrash(
    entries: any[],
    space_name: string,
    userShortname: string
): Promise<{ success: boolean; errorMessage?: string }> {
    try {
        const records = entries.map((entry) => {
            const moveResourceType = entry.resource_type;
            const moveNewSubpath = moveResourceType === ResourceType.folder
                ? (entry.subpath.split("/").slice(0, -1).join("-") || '/')
                : entry.subpath;

            const moveAttrb = {
                src_space_name: space_name,
                src_subpath: moveNewSubpath,
                src_shortname: entry.shortname,
                dest_space_name: 'personal',
                dest_subpath: `/people/${userShortname}/trash/${space_name}/${moveNewSubpath}`.replaceAll('//', '/'),
                dest_shortname: entry.shortname,
            };

            return {
                resource_type: moveResourceType,
                shortname: entry.shortname,
                subpath: moveNewSubpath,
                attributes: moveAttrb,
            };
        });

        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.move,
            records: records,
        });
        showToast(Level.info, `Entries moved to trash successfully`);
        return { success: true };
    } catch (error: any) {
        showToast(Level.warn, `Failed to move entries to trash!`);
        return { success: false, errorMessage: error.message };
    }
}