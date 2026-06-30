<script lang="ts">
    import { _ } from "svelte-i18n";
    import {
        Dmart,
        type ActionRequestRecord,
        type QueryRequest,
        QueryType,
        RequestType,
        ResourceType,
    } from "@edraj/tsdmart";
    import {
        Button,
        Card,
        Checkbox,
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
    import {
        ArrowLeftOutline,
        SearchOutline,
        TrashBinOutline,
        RefreshOutline,
        CloseOutline,
    } from "flowbite-svelte-icons";
    import { goto } from "@roxi/routify";
    import Prism from "@/components/Prism.svelte";
    import { getSpaces } from "@/lib/dmart_services";
    import { deleteEntry } from "@/utils/entryManagement";
    import { checkAccess } from "@/utils/checkAccess";
    import { Level, showToast } from "@/utils/toast";

    $goto;

    type SearchType = "shortname" | "email" | "msisdn";

    type OwnedEntry = {
        key: string;
        space_name: string;
        subpath: string;
        shortname: string;
        resource_type: ResourceType;
        raw: any;
    };

    let searchType: SearchType = $state("shortname");
    let searchValue: string = $state("");

    let userMatches: any[] = $state([]);
    let userSearched: boolean = $state(false);
    let selectedUserShortname: string | null = $state(null);

    let userEntry: any = $state(null);
    let ownedEntries: OwnedEntry[] = $state([]);
    let ownedSearched: boolean = $state(false);

    let selectedKeys: Record<string, boolean> = $state({});
    let batchSize: number = $state(10);

    let isSearching: boolean = $state(false);
    let isFetching: boolean = $state(false);
    let isDeleting: boolean = $state(false);
    let deleteProgress: { done: number; total: number } = $state({
        done: 0,
        total: 0,
    });

    let confirmOpen: boolean = $state(false);
    let pendingDelete: {
        targets: OwnedEntry[];
        mode: "one" | "many" | "retry";
    } | null = $state(null);

    type FailedDelete = {
        entry: OwnedEntry;
        error: string;
    };
    let failedDeletes: FailedDelete[] = $state([]);
    let lastRunSucceeded: number = $state(0);
    let lastRunFailed: number = $state(0);
    let showLastRunReport: boolean = $state(false);

    let deleteUserOpen: boolean = $state(false);
    let isDeletingUser: boolean = $state(false);

    let lastFetchedShortname: string | null = $state(null);
    let fetchedAll: boolean = $state(false);

    const OWNED_PAGE_SIZE = 1000;

    const selectedUser = $derived(
        userMatches.find((u) => u.shortname === selectedUserShortname) ?? null,
    );

    const selectedTargets = $derived(
        ownedEntries.filter((e) => selectedKeys[e.key]),
    );

    const allSelected = $derived(
        ownedEntries.length > 0 &&
            ownedEntries.every((e) => selectedKeys[e.key]),
    );

    let forceDelete: boolean = $state(false);

    // pendingDelete is inferred as `never` inside $derived (Svelte 5/TS); cast to any.
    const showForce = $derived(
        ((pendingDelete as any)?.targets ?? []).some(
            (t: any) => t.resource_type === ResourceType.folder || t.resource_type === ResourceType.user,
        ),
    );

    async function handleSearchUser() {
        if (!searchValue.trim()) {
            return;
        }
        isSearching = true;
        userEntry = null;
        ownedEntries = [];
        ownedSearched = false;
        selectedKeys = {};
        selectedUserShortname = null;
        lastFetchedShortname = null;
        failedDeletes = [];
        showLastRunReport = false;
        try {
            const query: QueryRequest = {
                type: QueryType.search,
                space_name: "management",
                subpath: "users",
                exact_subpath: true,
                search: `@${searchType}:${searchValue.trim()}`,
                retrieve_json_payload: true,
                limit: 50,
                offset: 0,
            };
            const res = await Dmart.query(query);
            userMatches = res?.records ?? [];
            userSearched = true;
            if (userMatches.length === 1) {
                selectedUserShortname = userMatches[0].shortname;
            }
        } catch (e) {
            showToast(Level.warn, "User search failed");
            userMatches = [];
            userSearched = true;
        } finally {
            isSearching = false;
        }
    }

    async function handleFetch() {
        if (!selectedUser) {
            return;
        }
        isFetching = true;
        ownedEntries = [];
        selectedKeys = {};
        try {
            userEntry = await Dmart.retrieveEntry({
                resource_type: ResourceType.user,
                space_name: "management",
                subpath: "users",
                shortname: selectedUser.shortname,
                retrieve_json_payload: true,
                retrieve_attachments: false,
                validate_schema: null,
            });
        } catch (e) {
            showToast(Level.warn, "Failed to fetch user entry");
            userEntry = null;
            isFetching = false;
            return;
        }
        await loadOwnedEntries(selectedUser.shortname);
        isFetching = false;
    }

    async function loadOwnedEntries(userShortname: string) {
        ownedSearched = false;
        fetchedAll = false;
        let allFetched = true;
        try {
            const spacesResp = await getSpaces();
            const spaceList = spacesResp?.records ?? [];
            const results = await Promise.all(
                spaceList.map(async (space: any) => {
                    // Paginate per space — a user owning more than one page of
                    // entries in a single space must not be silently truncated.
                    // We stop when a page returns fewer than the page size, or
                    // when one page throws (and mark allFetched=false so the
                    // post-delete user-account prompt can't false-positive).
                    const acc: OwnedEntry[] = [];
                    let offset = 0;
                    while (true) {
                        try {
                            const res = await Dmart.query({
                                type: QueryType.search,
                                space_name: space.shortname,
                                subpath: "/",
                                exact_subpath: false,
                                search: `@owner_shortname:${userShortname}`,
                                retrieve_json_payload: false,
                                limit: OWNED_PAGE_SIZE,
                                offset,
                            });
                            const records = res?.records ?? [];
                            for (const r of records) {
                                acc.push({
                                    key: `${space.shortname}|${r.subpath ?? "/"}|${r.shortname}`,
                                    space_name: space.shortname,
                                    subpath: r.subpath ?? "/",
                                    shortname: r.shortname,
                                    resource_type: r.resource_type as ResourceType,
                                    raw: r,
                                });
                            }
                            if (records.length < OWNED_PAGE_SIZE) {
                                break;
                            }
                            offset += OWNED_PAGE_SIZE;
                        } catch (e) {
                            allFetched = false;
                            break;
                        }
                    }
                    return acc;
                }),
            );
            ownedEntries = results.flat();
        } catch (e) {
            showToast(Level.warn, "Failed to load owned entries");
            ownedEntries = [];
            allFetched = false;
        }
        fetchedAll = allFetched;
        ownedSearched = true;
    }

    function toggleAll(checked: boolean) {
        const next: Record<string, boolean> = {};
        if (checked) {
            for (const e of ownedEntries) {
                next[e.key] = true;
            }
        }
        selectedKeys = next;
    }

    function askDeleteOne(entry: OwnedEntry) {
        forceDelete = false;
        pendingDelete = { targets: [entry], mode: "one" };
        confirmOpen = true;
    }

    function askDeleteSelected() {
        if (selectedTargets.length === 0) {
            return;
        }
        forceDelete = false;
        pendingDelete = { targets: selectedTargets, mode: "many" };
        confirmOpen = true;
    }

    function askDeleteAll() {
        if (ownedEntries.length === 0) {
            return;
        }
        forceDelete = false;
        pendingDelete = { targets: [...ownedEntries], mode: "many" };
        confirmOpen = true;
    }

    function chunk<T>(arr: T[], size: number): T[][] {
        const out: T[][] = [];
        const s = Math.max(1, Math.floor(size) || 1);
        for (let i = 0; i < arr.length; i += s) {
            out.push(arr.slice(i, i + s));
        }
        return out;
    }

    function deleteTargetSubpath(entry: OwnedEntry): string {
        // `entry.subpath` is the parent path returned by the query response —
        // already what Dmart.request expects, for both folders and non-folders.
        return entry.subpath || "/";
    }

    function extractError(input: any): string {
        if (!input) {
            return "Unknown error";
        }
        if (typeof input === "string") {
            return input;
        }
        if (input.response?.data?.error) {
            return typeof input.response.data.error === "string"
                ? input.response.data.error
                : JSON.stringify(input.response.data.error);
        }
        if (input.message) {
            return input.message;
        }
        return JSON.stringify(input);
    }

    async function deleteChunkGrouped(
        c: OwnedEntry[],
    ): Promise<{
        succeededKeys: Set<string>;
        failures: FailedDelete[];
    }> {
        const succeededKeys = new Set<string>();
        const failures: FailedDelete[] = [];

        const bySpace = new Map<string, OwnedEntry[]>();
        for (const entry of c) {
            const list = bySpace.get(entry.space_name) ?? [];
            list.push(entry);
            bySpace.set(entry.space_name, list);
        }

        await Promise.all(
            Array.from(bySpace.entries()).map(async ([space_name, items]) => {
                const records: ActionRequestRecord[] = items.map((e) => ({
                    resource_type: e.resource_type,
                    shortname: e.shortname,
                    subpath: deleteTargetSubpath(e),
                    attributes: {},
                }));
                try {
                    const response = await Dmart.request({
                        space_name,
                        request_type: RequestType.delete,
                        force: showForce && forceDelete,
                        records,
                    } as any);
                    if (response?.status === "success") {
                        for (const e of items) {
                            succeededKeys.add(e.key);
                        }
                    } else {
                        const err = extractError(response?.error ?? response);
                        for (const e of items) {
                            failures.push({ entry: e, error: err });
                        }
                    }
                } catch (err: any) {
                    const msg = extractError(err);
                    for (const e of items) {
                        failures.push({ entry: e, error: msg });
                    }
                }
            }),
        );

        return { succeededKeys, failures };
    }

    async function confirmDelete() {
        if (!pendingDelete) {
            return;
        }
        const targets = pendingDelete.targets;
        isDeleting = true;
        deleteProgress = { done: 0, total: targets.length };
        const chunks = chunk(targets, batchSize);
        const deletedKeys = new Set<string>();
        const newFailures: FailedDelete[] = [];
        for (const c of chunks) {
            const { succeededKeys, failures } = await deleteChunkGrouped(c);
            for (const k of succeededKeys) {
                deletedKeys.add(k);
            }
            for (const f of failures) {
                newFailures.push(f);
            }
            deleteProgress = {
                done: deleteProgress.done + c.length,
                total: targets.length,
            };
        }
        if (newFailures.length > 0) {
            showToast(
                Level.warn,
                `${newFailures.length} delete(s) failed`,
            );
        } else if (targets.length > 0) {
            showToast(Level.info, `Deleted ${targets.length} entry(ies)`);
        }

        const targetKeys = new Set(targets.map((t) => t.key));
        const carriedFailures = failedDeletes.filter(
            (f) => !targetKeys.has(f.entry.key),
        );
        failedDeletes = [...carriedFailures, ...newFailures];

        lastRunSucceeded = targets.length - newFailures.length;
        lastRunFailed = newFailures.length;
        showLastRunReport = true;

        ownedEntries = ownedEntries.filter((e) => !deletedKeys.has(e.key));
        const remainingSelection: Record<string, boolean> = {};
        for (const k of Object.keys(selectedKeys)) {
            if (!deletedKeys.has(k) && selectedKeys[k]) {
                remainingSelection[k] = true;
            }
        }
        selectedKeys = remainingSelection;
        isDeleting = false;
        confirmOpen = false;
        pendingDelete = null;

        maybePromptDeleteUser();
    }

    function maybePromptDeleteUser() {
        // `fetchedAll` is required so a paginated search that bailed out part-way
        // (or hit an error mid-pages) can't trigger a false "all entries gone"
        // signal. The in-memory ownedEntries list reflects only what we saw.
        if (
            userEntry &&
            selectedUser &&
            fetchedAll &&
            ownedEntries.length === 0 &&
            failedDeletes.length === 0 &&
            lastRunFailed === 0 &&
            lastRunSucceeded > 0
        ) {
            deleteUserOpen = true;
        }
    }

    async function confirmDeleteUser() {
        if (!selectedUser || isDeletingUser) {
            return;
        }
        isDeletingUser = true;
        const shortname = selectedUser.shortname;
        const result = await deleteEntry(
            userEntry,
            "management",
            "users",
            ResourceType.user,
        );
        isDeletingUser = false;
        deleteUserOpen = false;

        if (result.success) {
            userEntry = null;
            userMatches = [];
            userSearched = false;
            ownedSearched = false;
            ownedEntries = [];
            selectedUserShortname = null;
            lastFetchedShortname = null;
            searchValue = "";
            showLastRunReport = false;
            lastRunSucceeded = 0;
            lastRunFailed = 0;
        } else {
            failedDeletes = [
                ...failedDeletes,
                {
                    entry: {
                        key: `management|users|${shortname}`,
                        space_name: "management",
                        subpath: "users",
                        shortname,
                        resource_type: ResourceType.user,
                        raw: userEntry,
                    },
                    error:
                        typeof result.errorMessage === "string"
                            ? result.errorMessage
                            : JSON.stringify(
                                  result.errorMessage ?? "Unknown error",
                              ),
                },
            ];
            lastRunFailed = lastRunFailed + 1;
            showLastRunReport = true;
        }
    }

    function cancelDeleteUser() {
        if (isDeletingUser) {
            return;
        }
        deleteUserOpen = false;
    }

    function askRetryFailed() {
        if (failedDeletes.length === 0) {
            return;
        }
        forceDelete = false;
        pendingDelete = {
            targets: failedDeletes.map((f) => f.entry),
            mode: "retry",
        };
        confirmOpen = true;
    }

    function clearFailed() {
        failedDeletes = [];
    }

    function dismissLastRunReport() {
        showLastRunReport = false;
    }

    function cancelDelete() {
        if (isDeleting) {
            return;
        }
        confirmOpen = false;
        pendingDelete = null;
        forceDelete = false;
    }

    function canDeleteEntry(entry: OwnedEntry): boolean {
        return checkAccess(
            "delete",
            entry.space_name,
            entry.subpath,
            entry.resource_type,
        );
    }

    $effect(() => {
        const sn = selectedUserShortname;
        if (!sn || sn === lastFetchedShortname || isFetching) {
            return;
        }
        lastFetchedShortname = sn;
        handleFetch();
    });
</script>

<div class="container mx-auto p-8">
    <button
        class="flex items-center gap-2 text-gray-600 hover:text-primary-600 mb-6 transition-colors"
        onclick={() => $goto("/management/tools")}
    >
        <ArrowLeftOutline size="sm" />
        <span>Back to Tools</span>
    </button>

    <div class="flex items-center gap-3 mb-8">
        <div class="p-3 bg-red-100 rounded-full">
            <TrashBinOutline class="w-8 h-8 text-red-600" />
        </div>
        <div>
            <h1 class="text-2xl font-bold">{$_("entry_deletion")}</h1>
            <p class="text-gray-500">{$_("entry_deletion_description")}</p>
        </div>
    </div>

    <!-- Search card -->
    <Card class="min-w-full p-4 mb-6">
        <div class="grid grid-cols-1 md:grid-cols-12 gap-4 items-end">
            <div class="md:col-span-3">
                <Label for="search_type" class="mb-2"
                    >{$_("search_user_by")}</Label
                >
                <Select id="search_type" bind:value={searchType}>
                    <option value="shortname">{$_("shortname")}</option>
                    <option value="email">{$_("email")}</option>
                    <option value="msisdn">{$_("msisdn")}</option>
                </Select>
            </div>
            <div class="md:col-span-7">
                <Label for="search_value" class="mb-2">{$_("search")}</Label>
                <Input
                    id="search_value"
                    type="text"
                    bind:value={searchValue}
                    placeholder="e.g. dmart"
                    onkeydown={(e) => {
                        if (e.key === "Enter") {
                            handleSearchUser();
                        }
                    }}
                />
            </div>
            <div class="md:col-span-2">
                <Button
                    class="w-full"
                    color="blue"
                    onclick={handleSearchUser}
                    disabled={isSearching || !searchValue.trim()}
                >
                    <SearchOutline size="sm" class="me-2" />
                    {isSearching ? "..." : $_("search")}
                </Button>
            </div>
        </div>
    </Card>

    <!-- Matches list -->
    {#if userSearched}
        {#if userMatches.length === 0}
            <p class="text-gray-500 text-center mb-6">
                {$_("no_users_found")}
            </p>
        {:else}
            <Card class="min-w-full p-4 mb-6">
                <h3 class="text-lg font-semibold mb-3">
                    {$_("select_user_match")} ({userMatches.length})
                </h3>
                <div class="flex flex-col gap-2 max-h-64 overflow-auto">
                    {#each userMatches as match (match.shortname)}
                        <label
                            class="flex items-center gap-3 p-2 rounded border hover:bg-gray-50 cursor-pointer"
                        >
                            <input
                                type="radio"
                                name="user_match"
                                value={match.shortname}
                                bind:group={selectedUserShortname}
                            />
                            <div class="flex flex-col">
                                <span class="font-medium"
                                    >{match.shortname}</span
                                >
                                <span class="text-xs text-gray-500">
                                    {match.attributes?.payload?.body?.email ??
                                        match.attributes?.email ??
                                        ""}
                                    {#if match.attributes?.payload?.body?.msisdn ?? match.attributes?.msisdn}
                                        · {match.attributes?.payload?.body
                                            ?.msisdn ?? match.attributes?.msisdn}
                                    {/if}
                                </span>
                            </div>
                        </label>
                    {/each}
                </div>
                {#if isFetching}
                    <p class="mt-4 text-sm text-gray-500 text-right">
                        {$_("fetch")}...
                    </p>
                {/if}
            </Card>
        {/if}
    {/if}

    <!-- User JSON view -->
    {#if userEntry}
        <Card class="min-w-full p-4 mb-6">
            <h3 class="text-lg font-semibold mb-3">{$_("details")}</h3>
            <div class="max-h-96 overflow-auto">
                <Prism code={userEntry} />
            </div>
        </Card>
    {/if}

    <!-- Owned entries table + batch + bulk actions -->
    {#if userEntry && ownedSearched}
        <Card class="min-w-full p-4">
            <div class="flex flex-col md:flex-row md:items-end md:justify-between gap-4 mb-4">
                <div>
                    <h3 class="text-lg font-semibold">
                        {$_("owned_entries")} ({ownedEntries.length})
                    </h3>
                </div>
                <div class="flex items-end gap-3">
                    <div>
                        <Label for="batch_size" class="mb-2"
                            >{$_("batch_size")}</Label
                        >
                        <Input
                            id="batch_size"
                            type="number"
                            min="1"
                            max="100"
                            class="w-24"
                            bind:value={batchSize}
                        />
                    </div>
                    <Button
                        color="red"
                        onclick={askDeleteSelected}
                        disabled={selectedTargets.length === 0 || isDeleting}
                    >
                        <TrashBinOutline size="sm" class="me-2" />
                        {$_("delete_selected")} ({selectedTargets.length})
                    </Button>
                    <Button
                        color="red"
                        onclick={askDeleteAll}
                        disabled={ownedEntries.length === 0 || isDeleting}
                    >
                        <TrashBinOutline size="sm" class="me-2" />
                        {$_("delete_all_owned")}
                    </Button>
                </div>
            </div>

            {#if isDeleting}
                <p class="text-sm text-gray-600 mb-3">
                    {$_("deleting_progress", {
                        values: {
                            done: deleteProgress.done,
                            total: deleteProgress.total,
                        },
                    })}
                </p>
            {/if}

            {#if ownedEntries.length === 0}
                <p class="text-gray-500 text-center py-6">
                    {$_("no_owned_entries")}
                </p>
            {:else}
                <Table>
                    <TableHead>
                        <TableHeadCell class="w-10">
                            <Checkbox
                                checked={allSelected}
                                onchange={(e: any) =>
                                    toggleAll(e.currentTarget.checked)}
                            />
                        </TableHeadCell>
                        <TableHeadCell>{$_("space_name")}</TableHeadCell>
                        <TableHeadCell>{$_("subpath")}</TableHeadCell>
                        <TableHeadCell>{$_("shortname")}</TableHeadCell>
                        <TableHeadCell>{$_("resource_type")}</TableHeadCell>
                        <TableHeadCell>{$_("actions")}</TableHeadCell>
                    </TableHead>
                    <TableBody>
                        {#each ownedEntries as entry (entry.key)}
                            <TableBodyRow>
                                <TableBodyCell>
                                    <Checkbox
                                        checked={!!selectedKeys[entry.key]}
                                        onchange={(e: any) => {
                                            selectedKeys = {
                                                ...selectedKeys,
                                                [entry.key]:
                                                    e.currentTarget.checked,
                                            };
                                        }}
                                    />
                                </TableBodyCell>
                                <TableBodyCell>{entry.space_name}</TableBodyCell>
                                <TableBodyCell>{entry.subpath}</TableBodyCell>
                                <TableBodyCell>{entry.shortname}</TableBodyCell>
                                <TableBodyCell
                                    >{entry.resource_type}</TableBodyCell
                                >
                                <TableBodyCell>
                                    <Button
                                        size="xs"
                                        color="red"
                                        onclick={() => askDeleteOne(entry)}
                                        disabled={isDeleting ||
                                            !canDeleteEntry(entry)}
                                        title={canDeleteEntry(entry)
                                            ? ""
                                            : "No delete permission"}
                                    >
                                        <TrashBinOutline size="sm" />
                                    </Button>
                                </TableBodyCell>
                            </TableBodyRow>
                        {/each}
                    </TableBody>
                </Table>
            {/if}
        </Card>
    {/if}

    <!-- Last run report + failures -->
    {#if showLastRunReport || failedDeletes.length > 0}
        <Card class="min-w-full p-4 mt-6">
            {#if showLastRunReport}
                <div class="flex items-start justify-between gap-4 mb-3">
                    <div>
                        <h3 class="text-lg font-semibold">
                            {$_("delete_results")}
                        </h3>
                        <p class="text-sm">
                            <span class="text-green-700 font-medium"
                                >{$_("succeeded")}: {lastRunSucceeded}</span
                            >
                            <span class="mx-2 text-gray-400">·</span>
                            <span class="text-red-700 font-medium"
                                >{$_("failed")}: {lastRunFailed}</span
                            >
                        </p>
                    </div>
                    <button
                        class="text-gray-500 hover:text-gray-800"
                        onclick={dismissLastRunReport}
                        aria-label="Dismiss"
                    >
                        <CloseOutline size="sm" />
                    </button>
                </div>
            {/if}

            {#if failedDeletes.length > 0}
                <div class="flex items-end justify-between gap-3 mb-3">
                    <h4 class="text-md font-semibold text-red-700">
                        {$_("failed_deletes")} ({failedDeletes.length})
                    </h4>
                    <div class="flex gap-2">
                        <Button
                            size="xs"
                            color="alternative"
                            onclick={clearFailed}
                            disabled={isDeleting}
                        >
                            {$_("clear_failed")}
                        </Button>
                        <Button
                            size="xs"
                            color="red"
                            onclick={askRetryFailed}
                            disabled={isDeleting}
                        >
                            <RefreshOutline size="sm" class="me-2" />
                            {$_("retry_failed")}
                        </Button>
                    </div>
                </div>
                <Table>
                    <TableHead>
                        <TableHeadCell>{$_("space_name")}</TableHeadCell>
                        <TableHeadCell>{$_("subpath")}</TableHeadCell>
                        <TableHeadCell>{$_("shortname")}</TableHeadCell>
                        <TableHeadCell>{$_("resource_type")}</TableHeadCell>
                        <TableHeadCell class="w-1/3">{$_("error")}</TableHeadCell>
                    </TableHead>
                    <TableBody>
                        {#each failedDeletes as f (f.entry.key)}
                            <TableBodyRow>
                                <TableBodyCell
                                    >{f.entry.space_name}</TableBodyCell
                                >
                                <TableBodyCell>{f.entry.subpath}</TableBodyCell>
                                <TableBodyCell
                                    >{f.entry.shortname}</TableBodyCell
                                >
                                <TableBodyCell
                                    >{f.entry.resource_type}</TableBodyCell
                                >
                                <TableBodyCell
                                    class="text-red-600 text-xs break-all"
                                    >{f.error}</TableBodyCell
                                >
                            </TableBodyRow>
                        {/each}
                    </TableBody>
                </Table>
            {/if}
        </Card>
    {/if}

    <!-- Confirm modal -->
    <Modal bind:open={confirmOpen} size="md" title={$_("confirm")}>
        {#if pendingDelete}
            {#if pendingDelete.mode === "one"}
                <p class="text-center mb-4">
                    {$_("confirm_delete_one")}
                </p>
                <p class="text-center text-sm text-gray-600">
                    <span class="font-medium"
                        >{pendingDelete.targets[0].space_name}</span
                    >
                    /
                    <span class="font-medium"
                        >{pendingDelete.targets[0].subpath}</span
                    >
                    /
                    <span class="font-bold"
                        >{pendingDelete.targets[0].shortname}</span
                    >
                </p>
            {:else}
                <p class="text-center mb-4">
                    {pendingDelete.mode === "retry"
                        ? $_("confirm_retry_failed", {
                              values: {
                                  count: pendingDelete.targets.length,
                                  batch: Math.max(
                                      1,
                                      Math.floor(batchSize) || 1,
                                  ),
                              },
                          })
                        : $_("confirm_delete_many", {
                              values: {
                                  count: pendingDelete.targets.length,
                                  batch: Math.max(
                                      1,
                                      Math.floor(batchSize) || 1,
                                  ),
                              },
                          })}
                </p>
            {/if}
        {/if}

        {#if showForce && !isDeleting}
            <label class="flex items-start gap-2 mt-2 mb-2 text-sm cursor-pointer justify-center">
                <input type="checkbox" bind:checked={forceDelete} class="mt-0.5" />
                <span>
                    <span class="font-semibold">{$_("force_delete")}</span>
                    <span class="block text-gray-600">{$_("force_delete_help")}</span>
                </span>
            </label>
        {/if}

        {#if isDeleting}
            <p class="text-center text-sm text-gray-600 mb-2">
                {$_("deleting_progress", {
                    values: {
                        done: deleteProgress.done,
                        total: deleteProgress.total,
                    },
                })}
            </p>
        {/if}

        <div class="flex justify-between w-full">
            <Button
                color="alternative"
                onclick={cancelDelete}
                disabled={isDeleting}>{$_("cancel")}</Button
            >
            <Button color="red" onclick={confirmDelete} disabled={isDeleting}>
                {isDeleting ? "..." : $_("delete")}
            </Button>
        </div>
    </Modal>

    <!-- Delete user account modal (auto-prompts after clean run) -->
    <Modal bind:open={deleteUserOpen} size="md" title={$_("delete_user")}>
        <p class="text-center mb-4">
            {$_("confirm_delete_user")}
        </p>
        {#if selectedUser}
            <p class="text-center text-sm text-gray-600 mb-2">
                <span class="font-bold">{selectedUser.shortname}</span>
            </p>
        {/if}
        <div class="flex justify-between w-full">
            <Button
                color="alternative"
                onclick={cancelDeleteUser}
                disabled={isDeletingUser}
            >
                {$_("keep_user")}
            </Button>
            <Button
                color="red"
                onclick={confirmDeleteUser}
                disabled={isDeletingUser}
            >
                {isDeletingUser ? "..." : $_("delete_user")}
            </Button>
        </div>
    </Modal>
</div>
