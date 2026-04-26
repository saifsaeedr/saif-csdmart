<script lang="ts">
  import type { Datatable } from "./createDatatable.svelte";
  import type { Snippet } from "svelte";

  let {
    propDatatable = $bindable<Datatable>(),
    propColumn,
    children,
  }: {
    propDatatable: Datatable;
    propColumn: string;
    children: Snippet;
  } = $props();

  function toggle() {
    if (propDatatable.stringSortBy === propColumn) {
      propDatatable.stringSortOrder =
        propDatatable.stringSortOrder === "ascending" ? "descending" : "ascending";
    } else {
      propDatatable.stringSortBy = propColumn;
      propDatatable.stringSortOrder = "ascending";
    }
  }

  let isActive = $derived(propDatatable.stringSortBy === propColumn);
  let isAsc = $derived(propDatatable.stringSortOrder === "ascending");
</script>

<button
  type="button"
  class="inline-flex items-center gap-1 cursor-pointer whitespace-nowrap select-none"
  onclick={toggle}
>
  <svg class="w-3 h-3 mr-1 shrink-0" viewBox="0 0 16 16" aria-hidden="true">
    <path
      d="M4 6 l4 -4 l4 4"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      class:opacity-100={isActive && isAsc}
      class:opacity-30={!(isActive && isAsc)}
    />
    <path
      d="M4 10 l4 4 l4 -4"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      class:opacity-100={isActive && !isAsc}
      class:opacity-30={!(isActive && !isAsc)}
    />
  </svg>
  {@render children()}
</button>
