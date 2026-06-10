<script lang="ts">
    import { Card } from "flowbite-svelte";
    import { onMount } from "svelte";
    import FormField from "./FormField.svelte";
    import { unionKeys } from "@/utils/renderer/rendererUtils";

    let {
        content = $bindable({}),
        schema = undefined,
    }: {
        content: any;
        schema?: any;
    } = $props();

    onMount(() => {
        if (schema && schema.properties) {
            initializeContent(schema.properties);
        }
    });

    // Seed create-time defaults from the schema. Only ever *adds* missing keys,
    // so any prop already present in the data (declared or not) is preserved.
    function initializeContent(properties) {
        for (const key in properties) {
            const prop = properties[key];

            if (content[key] !== undefined) continue;

            if (prop.type === "string") {
                content[key] = prop.default || "";
            } else if (prop.type === "number" || prop.type === "integer") {
                content[key] = prop.default !== undefined ? prop.default : null;
            } else if (prop.type === "boolean") {
                content[key] = prop.default || false;
            } else if (prop.type === "array") {
                content[key] = prop.default || [];
            } else if (prop.type === "object" && prop.properties) {
                content[key] = {};
            } else {
                content[key] = null;
            }
        }

        content = { ...content };
    }

    // Render the union of schema-declared props and props actually present in
    // the data, so every prop in the record is shown — even undeclared ones.
    let isArrayContent = $derived(Array.isArray(content));
    let topLevelKeys = $derived(unionKeys(schema, content));
    let hasContent = $derived(
        isArrayContent ||
            (content !== null &&
                typeof content === "object" &&
                topLevelKeys.length > 0),
    );
</script>

<Card class="p-4 max-w-4xl mx-auto my-2">
    {#if hasContent}
        {#if schema?.title || schema?.description}
            {#if schema?.title}
                <h2 class="text-xl font-bold mb-1">{schema.title}</h2>
            {/if}
            {#if schema?.description}
                <p class="text-gray-600 mb-4">{schema.description}</p>
            {/if}
        {/if}

        <div class="space-y-6">
            {#if isArrayContent}
                <FormField
                    name={schema?.title || "Items"}
                    bind:value={content}
                    {schema}
                    idPath="root"
                />
            {:else}
                {#each topLevelKeys as key (key)}
                    <FormField
                        name={key}
                        bind:value={content[key]}
                        schema={schema?.properties?.[key]}
                        required={schema?.required?.includes(key) ?? false}
                        declared={!!schema?.properties?.[key]}
                        idPath={key}
                    />
                {/each}
            {/if}
        </div>
    {:else}
        <p class="text-gray-500 text-center py-4">No data to display.</p>
    {/if}
</Card>
