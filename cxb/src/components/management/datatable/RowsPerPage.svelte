<script lang="ts">
  import type { Datatable } from "./createDatatable.svelte";
  import type { Snippet } from "svelte";

  let {
    propDatatable = $bindable<Datatable>(),
    children,
    class: className = "",
  }: {
    propDatatable: Datatable;
    children: Snippet;
    class?: string;
  } = $props();
</script>

<select
  class={className}
  value={propDatatable.numberRowsPerPage.toString()}
  onchange={(e) => {
    propDatatable.numberRowsPerPage = Number((e.currentTarget as HTMLSelectElement).value);
  }}
>
  {@render children()}
</select>
