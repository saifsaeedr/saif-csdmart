<script lang="ts">
    import {
        Accordion,
        AccordionItem,
        Button,
        Checkbox,
        Input,
        Label,
        Select,
        Textarea,
    } from "flowbite-svelte";
    import FormField from "./FormField.svelte";
    import {
        buildDefaultForSchema,
        inferType,
        unionKeys,
    } from "@/utils/renderer/rendererUtils";

    let {
        name,
        value = $bindable(),
        schema = undefined,
        required = false,
        declared = true,
        hideLabel = false,
        depth = 0,
        idPath = name,
    }: {
        name: string;
        value: any;
        schema?: any;
        required?: boolean;
        declared?: boolean;
        hideLabel?: boolean;
        depth?: number;
        idPath?: string;
    } = $props();

    // The actual data structure wins over the schema so that no prop in the
    // record data is ever hidden; the schema only enriches scalar widgets.
    let effectiveType = $derived(
        Array.isArray(value)
            ? "array"
            : value !== null && typeof value === "object"
              ? "object"
              : (schema?.type ?? inferType(value)),
    );

    let isContainer = $derived(
        effectiveType === "object" || effectiveType === "array",
    );
    let showHeader = $derived(!hideLabel);
    let title = $derived(schema?.title || name);

    // Null containers are NEVER silently coerced — an entry whose declared
    // object/array prop is explicitly null must still save null unless the
    // user acts. Arrays initialize lazily on "Add Item" (addItem handles
    // value ?? []); null objects render a placeholder with an explicit
    // "Initialize" action below.

    // One stable id per array index, so the keyed {#each} preserves each
    // item's DOM/UI state (open accordions, focus) across removals — keying
    // by raw index would re-key every survivor after removing item 0.
    let nextItemId = 0;
    let itemIds: number[] = $state([]);
    $effect(() => {
        const len = Array.isArray(value) ? value.length : 0;
        while (itemIds.length < len) itemIds.push(nextItemId++);
        if (itemIds.length > len) itemIds.length = len;
    });

    function addItem() {
        const itemSchema = schema?.items;
        let newItem: any;
        if (itemSchema) {
            newItem = buildDefaultForSchema(itemSchema);
        } else if (Array.isArray(value) && value.length > 0) {
            const sample = value[0];
            if (Array.isArray(sample)) newItem = [];
            else if (sample !== null && typeof sample === "object")
                newItem = {};
            else if (typeof sample === "number") newItem = null;
            else if (typeof sample === "boolean") newItem = false;
            else newItem = "";
        } else {
            newItem = "";
        }
        value = [...(value ?? []), newItem];
    }

    function removeItem(index: number) {
        // Drop the removed slot's id BEFORE the value shrinks, so the sync
        // effect doesn't truncate the tail id and misattribute identities.
        itemIds.splice(index, 1);
        value = (value ?? []).filter((_: any, i: number) => i !== index);
    }
</script>

{#snippet headerContent()}
    {#if required}<span class="text-red-500">*</span>{/if}
    {title}
    {#if !declared}
        <span class="text-xs font-normal text-gray-400 italic ml-1"
            >(not in schema)</span
        >
    {/if}
{/snippet}

<div class="mb-4">
    {#if showHeader && !isContainer}
        <Label for={idPath} class="mb-1">{@render headerContent()}</Label>
        {#if schema?.description}
            <p class="text-xs text-gray-500 mb-1">{schema.description}</p>
        {/if}
    {/if}

    {#if effectiveType === "object"}
        {#if value && typeof value === "object" && !Array.isArray(value)}
            {#if depth === 0}
                <Accordion flush>
                    <AccordionItem>
                        {#snippet header()}<span class="font-medium"
                                >{@render headerContent()}</span
                            >{/snippet}
                        <div class="p-2 space-y-3">
                            {#each unionKeys(schema, value) as key (key)}
                                <FormField
                                    name={key}
                                    bind:value={value[key]}
                                    schema={schema?.properties?.[key]}
                                    required={schema?.required?.includes(key) ??
                                        false}
                                    declared={!!schema?.properties?.[key]}
                                    depth={depth + 1}
                                    idPath={`${idPath}.${key}`}
                                />
                            {/each}
                        </div>
                    </AccordionItem>
                </Accordion>
            {:else}
                <div class="border rounded-lg p-3 space-y-3">
                    {#if showHeader}
                        <div class="font-medium">{@render headerContent()}</div>
                    {/if}
                    {#each unionKeys(schema, value) as key (key)}
                        <FormField
                            name={key}
                            bind:value={value[key]}
                            schema={schema?.properties?.[key]}
                            required={schema?.required?.includes(key) ?? false}
                            declared={!!schema?.properties?.[key]}
                            depth={depth + 1}
                            idPath={`${idPath}.${key}`}
                        />
                    {/each}
                </div>
            {/if}
        {:else}
            <!-- Declared object that is currently null: initializing it is an
                 EXPLICIT user action — auto-coercing here would silently save
                 `{}` for a prop the user never touched. -->
            <div
                class="border rounded-lg p-3 flex items-center justify-between gap-2"
            >
                {#if showHeader}
                    <span class="font-medium">{@render headerContent()}</span>
                {/if}
                <span class="text-sm text-gray-400 italic">null</span>
                <Button size="xs" color="light" onclick={() => (value = {})}
                    >Initialize</Button
                >
            </div>
        {/if}
    {:else if effectiveType === "array"}
        <div class="border p-4 rounded-lg">
            <div class="flex justify-between items-center mb-2">
                {#if showHeader}
                    <h3 class="font-semibold">{@render headerContent()}</h3>
                {:else}
                    <span></span>
                {/if}
                <Button size="xs" color="blue" onclick={addItem}>Add Item</Button
                >
            </div>

            {#if Array.isArray(value) && value.length > 0}
                {#each value as _item, index (itemIds[index] ?? index)}
                    <div class="mt-2 p-2 bg-gray-50 rounded border">
                        <FormField
                            name={String(index)}
                            bind:value={value[index]}
                            schema={schema?.items}
                            declared={true}
                            hideLabel={true}
                            depth={depth + 1}
                            idPath={`${idPath}.${index}`}
                        />
                        <div class="flex justify-end mt-2">
                            <Button
                                size="xs"
                                color="red"
                                onclick={() => removeItem(index)}>Remove</Button
                            >
                        </div>
                    </div>
                {/each}
            {:else}
                <p class="text-gray-500 text-center py-2">No items added yet.</p>
            {/if}
        </div>
    {:else if effectiveType === "boolean"}
        <div class="flex items-center gap-2">
            <Checkbox
                id={idPath}
                checked={value ?? false}
                onchange={(e) =>
                    (value = (e.currentTarget || e.target).checked)}
            />
        </div>
    {:else if effectiveType === "number" || effectiveType === "integer"}
        <Input
            id={idPath}
            type="number"
            bind:value
            {required}
            min={schema?.minimum}
            max={schema?.maximum}
            step={effectiveType === "integer"
                ? 1
                : schema?.multipleOf || "any"}
        />
    {:else if schema?.format === "date-time" || schema?.format === "date"}
        <Input id={idPath} type="date" bind:value {required} />
    {:else if schema?.format === "time"}
        <Input id={idPath} type="time" bind:value {required} />
    {:else if schema?.format === "email"}
        <Input id={idPath} type="email" bind:value {required} />
    {:else if schema?.format === "uri"}
        <Input id={idPath} type="url" bind:value {required} />
    {:else if schema?.enum}
        <Select id={idPath} bind:value {required}>
            <option value="">Select an option</option>
            {#each schema.enum as option}
                <option value={option}>{option}</option>
            {/each}
        </Select>
    {:else if schema?.maxLength && schema.maxLength > 100}
        <Textarea id={idPath} rows={3} bind:value {required} />
    {:else}
        <Input
            id={idPath}
            type="text"
            bind:value
            {required}
            minlength={schema?.minLength}
            maxlength={schema?.maxLength}
            pattern={schema?.pattern}
        />
    {/if}
</div>
