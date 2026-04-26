<script lang="ts">
  import type { Datatable } from "./createDatatable.svelte";

  let {
    propDatatable = $bindable<Datatable>(),
    propNumberOfPages = $bindable<number>(1),
    maxPageDisplay = 5,
    propSize = "default",
  }: {
    propDatatable: Datatable;
    propNumberOfPages?: number;
    maxPageDisplay?: number;
    propSize?: "default" | "small" | "large";
  } = $props();

  let current = $derived(propDatatable.numberActivePage);

  let pages = $derived.by(() => {
    const total = Math.max(1, propNumberOfPages);
    const half = Math.floor(maxPageDisplay / 2);
    let start = Math.max(1, current - half);
    let end = Math.min(total, start + maxPageDisplay - 1);
    if (end - start + 1 < maxPageDisplay) {
      start = Math.max(1, end - maxPageDisplay + 1);
    }
    return Array.from({ length: end - start + 1 }, (_, i) => start + i);
  });

  let sizeClass = $derived(
    propSize === "small" ? "px-2 py-1 text-xs" : propSize === "large" ? "px-4 py-2 text-base" : "px-3 py-1.5 text-sm"
  );

  function go(p: number) {
    if (p < 1 || p > propNumberOfPages || p === current) return;
    propDatatable.numberActivePage = p;
  }
</script>

<nav>
  <ul class="inline-flex -space-x-px">
    <li>
      <button
        type="button"
        class="{sizeClass} border border-gray-300 bg-white text-gray-600 rounded-l hover:bg-gray-100 disabled:opacity-50"
        disabled={current <= 1}
        onclick={() => go(current - 1)}
      >‹</button>
    </li>
    {#each pages as p}
      <li>
        <button
          type="button"
          class="{sizeClass} border border-gray-300 hover:bg-gray-100 {p === current ? 'bg-blue-600 text-white border-blue-600 hover:bg-blue-700' : 'bg-white text-gray-700'}"
          onclick={() => go(p)}
        >{p}</button>
      </li>
    {/each}
    <li>
      <button
        type="button"
        class="{sizeClass} border border-gray-300 bg-white text-gray-600 rounded-r hover:bg-gray-100 disabled:opacity-50"
        disabled={current >= propNumberOfPages}
        onclick={() => go(current + 1)}
      >›</button>
    </li>
  </ul>
</nav>
