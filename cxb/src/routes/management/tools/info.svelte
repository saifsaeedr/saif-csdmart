<script lang="ts">
    import { Dmart } from "@edraj/tsdmart";
    import Table2Cols from "@/components/management/Table2Cols.svelte";
    import {
        Table,
        TableBody,
        TableBodyCell,
        TableBodyRow,
        TableHead,
        TableHeadCell,
    } from "flowbite-svelte";
    import {
        InfoCircleOutline,
        UserSettingsOutline,
        ArrowLeftOutline,
        CodeOutline,
    } from "flowbite-svelte-icons";
    import { onMount } from "svelte";
    import { goto } from "@roxi/routify";
    $goto;

    const TabMode = {
        settings: "settings",
        manifest: "manifest",
        plugins: "plugins",
    };

    // Records returned by GET /info/plugins. Each plugin row carries the
    // version it announces from its own binary (assembly attr, .so dlsym,
    // or subprocess info-response — see custom_plugins_sdk/README.md) plus
    // its wire type ("hook" | "api").
    type PluginRow = {
        shortname: string;
        version: string;
        type: string;
    };

    let activeTab = $state(TabMode.settings);
    let settings = $state({});
    let manifest = $state({});
    let plugins = $state<PluginRow[]>([]);
    let pluginsError = $state<string | null>(null);

    onMount(async () => {
        const _settings = await Dmart.getSettings();
        if (_settings.status === "success") {
            settings = _settings.attributes;
        }
        const _manifest = await Dmart.getManifest();
        if (_manifest.status === "success") {
            manifest = _manifest.attributes;
        }
        try {
            // Direct axios call against the same instance tsdmart configures,
            // intentionally NOT going through Dmart.getPlugins() (added in
            // tsdmart 5.3.4). The workspace yarn.lock currently pins
            // @edraj/tsdmart to 5.3.3, and bumping the cxb package.json
            // floor to ^5.3.4 has surfaced a not-yet-explained CI
            // regression in dmart's admin-login integration tests — see the
            // reverted commit on PR #32 for the trail. Calling axios here
            // dodges the version dependency entirely and ships the feature.
            const { data: _plugins } = await Dmart.axiosDmartInstance.get(
                "info/plugins", { headers: Dmart.getHeaders() });
            if (_plugins.status === "success" && Array.isArray(_plugins.records)) {
                plugins = _plugins.records.map((r: any) => ({
                    shortname: r.shortname ?? "",
                    version: r.attributes?.version ?? "0.0.0",
                    type: r.attributes?.type ?? "",
                }));
            } else {
                pluginsError = "Plugins endpoint returned no records.";
            }
        } catch (err: any) {
            // Older dmart servers (pre-/info/plugins) return 404 here. Surface
            // the failure in the tab body rather than swallowing — operators
            // can then upgrade the server.
            pluginsError = err?.message ?? "Failed to load plugins.";
        }
    });
</script>

<div class="mb-6 container mx-auto p-8">
    <button
        class="flex items-center gap-2 text-gray-600 hover:text-primary-600 mb-6 transition-colors"
        onclick={() => $goto("/management/tools")}
    >
        <ArrowLeftOutline size="sm" />
        <span>Back to Tools</span>
    </button>

    <div class="flex items-center gap-3 mb-8">
        <div class="p-3 bg-primary-100 rounded-full">
            <InfoCircleOutline class="w-8 h-8 text-primary-600" />
        </div>
        <div>
            <h1 class="text-2xl font-bold">Information</h1>
            <p class="text-gray-500">
                Get information about connected instance of Dmart.
            </p>
        </div>
    </div>

    <div class="border-b border-gray-200">
        <ul
            class="flex flex-wrap -mb-px text-sm font-medium text-center"
            role="tablist"
        >
            <li class="mr-2" role="presentation">
                <button
                    class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.settings
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                    type="button"
                    role="tab"
                    aria-selected={activeTab === TabMode.settings}
                    onclick={() => (activeTab = TabMode.settings)}
                >
                    <div class="flex items-center gap-2">
                        <UserSettingsOutline size="md" />
                        <p>Settings</p>
                    </div>
                </button>
            </li>
            <li class="mr-2" role="presentation">
                <button
                    class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.manifest
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                    type="button"
                    role="tab"
                    aria-selected={activeTab === TabMode.manifest}
                    onclick={() => (activeTab = TabMode.manifest)}
                >
                    <div class="flex items-center gap-2">
                        <InfoCircleOutline size="md" />
                        <p>Manifest</p>
                    </div>
                </button>
            </li>
            <li class="mr-2" role="presentation">
                <button
                    class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.plugins
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                    type="button"
                    role="tab"
                    aria-selected={activeTab === TabMode.plugins}
                    onclick={() => (activeTab = TabMode.plugins)}
                >
                    <div class="flex items-center gap-2">
                        <CodeOutline size="md" />
                        <p>Plugins</p>
                    </div>
                </button>
            </li>
        </ul>
    </div>

    <div>
        <div
            class={activeTab === TabMode.settings ? "" : "hidden"}
            role="tabpanel"
        >
            <Table2Cols bind:entry={settings} />
        </div>
        <div
            class={activeTab === TabMode.manifest ? "" : "hidden"}
            role="tabpanel"
        >
            <Table2Cols bind:entry={manifest} />
        </div>
        <div
            class={activeTab === TabMode.plugins ? "" : "hidden"}
            role="tabpanel"
        >
            {#if pluginsError}
                <div class="p-4 my-4 text-sm text-red-700 bg-red-100 rounded">
                    {pluginsError}
                </div>
            {:else if plugins.length === 0}
                <div class="p-4 my-4 text-sm text-gray-500">
                    No plugins are currently loaded.
                </div>
            {:else}
                <!-- Three columns: shortname, version, type. Versions come
                     from the plugin binary itself (assembly attr, .so symbol,
                     or subprocess info JSON) — not a config file. -->
                <div class="h-full" style="overflow-y: auto;">
                    <Table class="h-full" striped>
                        <TableHead>
                            <TableHeadCell>Shortname</TableHeadCell>
                            <TableHeadCell>Version</TableHeadCell>
                            <TableHeadCell>Type</TableHeadCell>
                        </TableHead>
                        <TableBody>
                            {#each plugins as p}
                                <TableBodyRow>
                                    <TableBodyCell>{p.shortname}</TableBodyCell>
                                    <TableBodyCell>{p.version}</TableBodyCell>
                                    <TableBodyCell>{p.type}</TableBodyCell>
                                </TableBodyRow>
                            {/each}
                        </TableBody>
                    </Table>
                </div>
            {/if}
        </div>
    </div>
</div>
