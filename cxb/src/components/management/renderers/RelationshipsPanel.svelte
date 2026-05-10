<script lang="ts">
    import { _ } from "svelte-i18n";
    import {
        Button,
        Input,
        Label,
        Modal,
        Select,
        Table,
        TableBody,
        TableBodyCell,
        TableBodyRow,
        TableHead,
        TableHeadCell,
    } from "flowbite-svelte";
    import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { JSONEditor, Mode } from "svelte-jsoneditor";
    import Prism from "@/components/Prism.svelte";
    import {
        getChildren,
        getChildrenAndSubChildren,
    } from "@/lib/dmart_services";
    import {
        PlusOutline,
        TrashBinSolid,
        PenSolid,
    } from "flowbite-svelte-icons";

    let {
        relationships = $bindable([]),
        space_name,
        subpath,
        resource_type,
        parent_shortname,
    }: {
        relationships: any[];
        space_name: string;
        subpath: string;
        resource_type: ResourceType;
        parent_shortname: string;
    } = $props();

    let isSaving = $state(false);

    let isEditing = $state(false);
    let editIndex = $state(-1);
    let showForm = $state(false);

    let relSpaceName = $state("");
    let relSubpath = $state("/");
    let relShortname = $state("");
    let relType = $state(ResourceType.content);
    let relSchemaShortname = $state("");

    let relAttributes: any = $state({ json: {} });

    let spaces: any[] = $state([]);
    let subpaths: string[] = $state([]);
    let shortnames: any[] = $state([]);
    let isLoadingSubpaths = $state(false);
    let isLoadingShortnames = $state(false);

    async function loadSpaces() {
        try {
            const result = await Dmart.getSpaces();
            spaces = result?.records || [];
        } catch (e) {
            spaces = [];
        }
    }

    async function loadSubpaths(spaceName: string) {
        if (!spaceName) {
            subpaths = [];
            return;
        }
        isLoadingSubpaths = true;
        try {
            const tempSubpaths: string[] = [];
            const rootChildren = await getChildren(spaceName, "/", 100);
            await getChildrenAndSubChildren(
                tempSubpaths,
                spaceName,
                "",
                rootChildren,
            );
            subpaths = tempSubpaths.reverse();
        } catch (e) {
            subpaths = [];
        } finally {
            isLoadingSubpaths = false;
        }
    }

    async function loadShortnames(spaceName: string, subpathVal: string) {
        if (!spaceName || !subpathVal) {
            shortnames = [];
            return;
        }
        isLoadingShortnames = true;
        try {
            const result = await getChildren(spaceName, subpathVal, 100);
            shortnames = (result.records || []).filter(
                (r: any) => r.resource_type !== "folder",
            );
        } catch (e) {
            shortnames = [];
        } finally {
            isLoadingShortnames = false;
        }
    }

    $effect(() => {
        if (spaces.length === 0) {
            loadSpaces();
        }
    });

    $effect(() => {
        if (relSpaceName) {
            loadSubpaths(relSpaceName);
        }
    });

    $effect(() => {
        if (relSpaceName && relSubpath) {
            loadShortnames(relSpaceName, relSubpath);
        }
    });

    function onSpaceChange() {
        relSubpath = "/";
        relShortname = "";
        shortnames = [];
    }

    function onSubpathChange() {
        relShortname = "";
    }

    function resetForm() {
        relSpaceName = "";
        relSubpath = "/";
        relShortname = "";
        relType = ResourceType.content;
        relSchemaShortname = "";
        relAttributes = { json: {} };
        isEditing = false;
        editIndex = -1;
        showForm = false;
    }

    function handleRenderMenu(items: any) {
        return items.filter(
            (item: any) => !["tree", "table"].includes(item.text),
        );
    }

    function populateFormForEdit(index: number) {
        const rel = relationships[index];
        const locator = rel.related_to || {};
        relSpaceName = locator.space_name || "";
        relSubpath = locator.subpath || "/";
        relShortname = locator.shortname || "";
        relType = locator.type || ResourceType.content;
        relSchemaShortname = locator.schema_shortname || "";
        relAttributes = { json: rel.attributes || {} };
        isEditing = true;
        editIndex = index;
        showForm = true;
    }

    function buildRelationship() {
        const locator: any = {
            type: relType,
            space_name: relSpaceName,
            subpath: relSubpath,
            shortname: relShortname,
        };
        if (relSchemaShortname) {
            locator.schema_shortname = relSchemaShortname;
        }

        let attrs = {};
        try {
            if (relAttributes?.json) {
                attrs = relAttributes.json;
            } else if (relAttributes?.text) {
                attrs = JSON.parse(relAttributes.text);
            }
        } catch {
            attrs = {};
        }

        return {
            related_to: locator,
            attributes: attrs,
        };
    }

    async function saveRelationships(updatedRelationships: any[]) {
        isSaving = true;
        try {
            await Dmart.request({
                space_name,
                request_type: RequestType.update,
                records: [
                    {
                        resource_type,
                        shortname: parent_shortname,
                        subpath,
                        attributes: {
                            relationships: updatedRelationships,
                        },
                    },
                ],
            });
            showToast(Level.info, "Relationships saved successfully!");
        } catch (e: any) {
            showToast(
                Level.warn,
                e.response?.data?.error?.message ||
                    "Failed to save relationships",
            );
        } finally {
            isSaving = false;
        }
    }

    async function addRelationship() {
        const rel = buildRelationship();
        if (!rel.related_to.space_name || !rel.related_to.shortname) return;

        let updated: any[];
        if (isEditing && editIndex >= 0) {
            updated = [...relationships];
            updated[editIndex] = rel;
        } else {
            updated = [...relationships, rel];
        }

        await saveRelationships(updated);
        relationships = updated;
        resetForm();
    }

    async function removeRelationship(index: number) {
        const updated = relationships.filter(
            (_: any, i: number) => i !== index,
        );
        await saveRelationships(updated);
        relationships = updated;
    }

    function getLocatorDisplay(rel: any) {
        const loc = rel.related_to || {};
        return `${loc.space_name || "?"}:${loc.subpath || "/"}/${loc.shortname || "?"}`;
    }

    let isDetailsOpen = $state(false);
    let detailsRel: any = $state(null);

    function openDetails(rel: any) {
        detailsRel = rel;
        isDetailsOpen = true;
    }
</script>

<div class="space-y-4 w-full p-4">
    {#if relationships && relationships.length > 0}
        <div class="w-full overflow-x-auto">
            <Table striped hoverable>
                <TableHead>
                    <TableHeadCell>Type</TableHeadCell>
                    <TableHeadCell>Space Name</TableHeadCell>
                    <TableHeadCell>Subpath</TableHeadCell>
                    <TableHeadCell>Shortname</TableHeadCell>
                    <TableHeadCell class="text-right">Actions</TableHeadCell>
                </TableHead>
                <TableBody>
                    {#each relationships as rel, index}
                        <TableBodyRow
                            class="cursor-pointer"
                            onclick={() => openDetails(rel)}
                        >
                            <TableBodyCell>
                                {rel.related_to?.type || "content"}
                            </TableBodyCell>
                            <TableBodyCell>
                                {rel.related_to?.space_name || "-"}
                            </TableBodyCell>
                            <TableBodyCell>
                                {rel.related_to?.subpath || "/"}
                            </TableBodyCell>
                            <TableBodyCell>
                                {rel.related_to?.shortname || "-"}
                            </TableBodyCell>
                            <TableBodyCell class="text-right">
                                <div
                                    class="inline-flex items-center gap-1"
                                    onclick={(e) => e.stopPropagation()}
                                    role="presentation"
                                >
                                    <Button
                                        size="xs"
                                        color="light"
                                        onclick={() =>
                                            populateFormForEdit(index)}
                                    >
                                        <PenSolid size="sm" />
                                    </Button>
                                    <Button
                                        size="xs"
                                        color="light"
                                        onclick={() =>
                                            removeRelationship(index)}
                                        disabled={isSaving}
                                    >
                                        <TrashBinSolid
                                            size="sm"
                                            class="text-red-500"
                                        />
                                    </Button>
                                </div>
                            </TableBodyCell>
                        </TableBodyRow>
                    {/each}
                </TableBody>
            </Table>
        </div>
    {:else}
        <p class="text-gray-500 text-center py-4">
            No relationships defined yet.
        </p>
    {/if}

    <div class="flex justify-center">
        <Button
            color="blue"
            outline
            onclick={() => {
                resetForm();
                showForm = true;
            }}
        >
            <PlusOutline size="sm" class="mr-2" />
            Add Relationship
        </Button>
    </div>
</div>

<Modal
    bind:open={showForm}
    size="lg"
    title={isEditing ? "Edit Relationship" : "New Relationship"}
    on:close={resetForm}
>
    <div class="space-y-3 w-full">
        <div>
            <Label for="rel-space">Space Name</Label>
            <Select
                id="rel-space"
                bind:value={relSpaceName}
                onchange={onSpaceChange}
            >
                <option value="">-- Select Space --</option>
                {#each spaces as space}
                    <option value={space.shortname}>{space.shortname}</option>
                {/each}
            </Select>
        </div>

        <div>
            <Label for="rel-subpath">Subpath</Label>
            <Select
                id="rel-subpath"
                bind:value={relSubpath}
                onchange={onSubpathChange}
                disabled={!relSpaceName || isLoadingSubpaths}
            >
                <option value="/">/</option>
                {#each subpaths as path}
                    <option value={path}>{path}</option>
                {/each}
            </Select>
            {#if isLoadingSubpaths}
                <p class="text-xs text-gray-400 mt-1">
                    Loading subpaths...
                </p>
            {/if}
        </div>

        <div>
            <Label for="rel-shortname">Shortname</Label>
            <Select
                id="rel-shortname"
                bind:value={relShortname}
                disabled={!relSpaceName || isLoadingShortnames}
            >
                <option value="">-- Select --</option>
                {#each shortnames as item}
                    <option value={item.shortname}>{item.shortname}</option>
                {/each}
            </Select>
            {#if isLoadingShortnames}
                <p class="text-xs text-gray-400 mt-1">
                    Loading entries...
                </p>
            {/if}
        </div>

        <div>
            <Label for="rel-type">Resource Type</Label>
            <Select id="rel-type" bind:value={relType}>
                {#each Object.values(ResourceType) as rt}
                    <option value={rt}>{rt}</option>
                {/each}
            </Select>
        </div>

        <div>
            <Label for="rel-schema">Schema Shortname (optional)</Label>
            <Input
                id="rel-schema"
                type="text"
                bind:value={relSchemaShortname}
                placeholder="Optional schema shortname"
            />
        </div>

        <div>
            <Label>Attributes</Label>
            <div
                class="border rounded-md overflow-hidden"
                style="min-height: 120px;"
            >
                <JSONEditor
                    onRenderMenu={handleRenderMenu}
                    mode={Mode.text}
                    bind:content={relAttributes}
                />
            </div>
        </div>
    </div>

    <div class="flex justify-end gap-2 w-full pt-4 border-t mt-4">
        <Button color="alternative" onclick={resetForm}>Cancel</Button>
        <Button
            color="blue"
            onclick={addRelationship}
            disabled={!relSpaceName || !relShortname || isSaving}
        >
            {#if isSaving}
                Saving...
            {:else}
                {isEditing ? "Update" : "Add"}
            {/if}
        </Button>
    </div>
</Modal>

<Modal bind:open={isDetailsOpen} size="lg" title="Relationship Details">
    {#if detailsRel}
        <div class="space-y-3 w-full">
            <div class="grid grid-cols-3 gap-2">
                <div class="font-medium text-gray-700">Type</div>
                <div class="col-span-2">
                    {detailsRel.related_to?.type || "content"}
                </div>
            </div>
            <div class="grid grid-cols-3 gap-2">
                <div class="font-medium text-gray-700">Space Name</div>
                <div class="col-span-2">
                    {detailsRel.related_to?.space_name || "-"}
                </div>
            </div>
            <div class="grid grid-cols-3 gap-2">
                <div class="font-medium text-gray-700">Subpath</div>
                <div class="col-span-2">
                    {detailsRel.related_to?.subpath || "/"}
                </div>
            </div>
            <div class="grid grid-cols-3 gap-2">
                <div class="font-medium text-gray-700">Shortname</div>
                <div class="col-span-2">
                    {detailsRel.related_to?.shortname || "-"}
                </div>
            </div>
            {#if detailsRel.related_to?.schema_shortname}
                <div class="grid grid-cols-3 gap-2">
                    <div class="font-medium text-gray-700">
                        Schema Shortname
                    </div>
                    <div class="col-span-2">
                        {detailsRel.related_to.schema_shortname}
                    </div>
                </div>
            {/if}
            <div>
                <p class="font-medium text-gray-700 mb-2">Attributes</p>
                <div class="max-h-80 overflow-auto">
                    <Prism code={detailsRel.attributes ?? {}} />
                </div>
            </div>
        </div>
    {/if}

    <div class="flex justify-end w-full pt-4 border-t mt-4">
        <Button
            color="alternative"
            onclick={() => (isDetailsOpen = false)}>Close</Button
        >
    </div>
</Modal>
