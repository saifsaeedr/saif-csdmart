<script lang="ts">
  import { onMount } from "svelte";
  import {
    getSpaceContents,
    getSpaces,
    getSpaceTags,
    searchInCatalog,
  } from "@/lib/dmart_services";
  import { getAllUsers } from "@/lib/dmart_services/users";
  import { goto } from "@roxi/routify";
  import { _, locale } from "@/i18n";
  import { derived as derivedStore } from "svelte/store";
  import { formatNumber, formatNumberInText } from "@/lib/helpers";
  import { QueryType } from "@edraj/tsdmart";
  import { user, getCurrentScope } from "@/stores/user";

  $goto;

  let isLoading = $state(true);
  let isStatsLoading = $state(true);
  let spaces = $state<any[]>([]);
  let filteredSpaces = $state<any[]>([]);
  let error: any = $state(null);
  let searchQuery = $state("");
  let sortBy = $state("name");
  let sortOrder = $state("asc");
  let filterCategory = $state("all");
  let filterTags = $state("all");
  let filterActive = $state("all");
  let showFilters = $state(false);
  let searchResults = $state<any[]>([]);
  let isSearching = $state(false);
  let searchTimeout: any;
  let spaceStats: any[] = [];
  let totalSpaceItems = $state(0);
  let totalUsers = $state(0);
  let spaceTags: Record<string, any> = $state({});

  const isRTL = derivedStore(
    locale,
    ($locale: any) => $locale === "ar" || $locale === "ku",
  );

  onMount(async () => {
    const scope = getCurrentScope();

    // Phase 1: Load spaces first and show them immediately
    try {
      const response = await getSpaces(false, scope);
      spaces = response.records || [];
      filteredSpaces = spaces;
    } catch (err) {
      console.error("Error fetching spaces:", err);
      error = $_("catalogs.error.failed_load");
    } finally {
      isLoading = false;
    }

    // Phase 2: Load stats, users, and tags in background
    try {
      const [usersResponse, ...statsArr] = await Promise.all([
        getAllUsers(0, 0),
        ...spaces.map(async (space: any) => {
          const data = await getSpaceContents(
            space.shortname,
            "/",
            scope,
            100,
            0,
            false,
            QueryType.counters,
          );
          const tags = await getSpaceTags(space.shortname);

          if (tags.status === "success" && tags.records.length > 0) {
            const tagData = tags.records[0]?.attributes;
            if (tagData?.tag_counts) {
              const sortedTags = Object.entries(tagData.tag_counts)
                .map(([name, count]: any) => ({ name, count }))
                .sort((a: any, b: any) => Number(b.count) - Number(a.count));
              spaceTags[space.shortname] = sortedTags;
            }
          } else {
            spaceTags[space.shortname] = [];
          }

          return {
            spaceName: space.shortname,
            total: data.attributes.total,
          };
        }),
      ]);

      totalUsers = usersResponse?.attributes?.total ?? 0;
      spaceStats = statsArr;
      totalSpaceItems = statsArr.reduce((sum: any, stat: any) => sum + stat.total, 0);
    } catch (err) {
      console.error("Error fetching stats:", err);
    } finally {
      isStatsLoading = false;
    }
  });

  function getTagsSpaces(shortname: any) {
    return spaceTags[shortname] || [];
  }

  function getSpaceStats(spaceShortname: any) {
    return (
      spaceStats.find((stat: any) => stat.spaceName === spaceShortname)?.total || 0
    );
  }

  function handleSpaceClick(space: any) {
    $goto("/catalogs/[space_name]", {
      space_name: space.shortname,
    });
  }

  function handleRecordClick(record: any) {
    const encodedSubpath = encodeURIComponent(record.subpath);

    $goto("/catalogs/[space_name]/[subpath]/[shortname]/[resource_type]", {
      space_name: record.attributes?.space_name,
      subpath: encodedSubpath,
      shortname: record.shortname,
      resource_type: record.resource_type,
    });
  }

  function getDisplayName(space: any): string {
    const displayname = space.attributes?.displayname;
    if (displayname) {
      return (
        displayname[$locale ?? ""] ||
        displayname.en ||
        displayname.ar ||
        space.shortname
      );
    }
    return space.shortname || $_("catalogs.unnamed_space");
  }

  function getDescription(space: any): string {
    const description = space.attributes?.description;
    if (description) {
      return (
        description[$locale ?? ""] ||
        description.en ||
        description.ar ||
        $_("catalogs.no_description")
      );
    }
    return $_("catalogs.no_description");
  }

  function getRecordDisplayName(record: any): string {
    const displayname = record.attributes?.displayname;
    if (displayname) {
      return (
        displayname[$locale ?? ""] ||
        displayname.en ||
        displayname.ar ||
        record.shortname
      );
    }
    if (
      record.resource_type === "ticket" &&
      record.attributes?.payload?.body?.title
    ) {
      return record.attributes.payload.body.title;
    }
    return record.shortname || $_("catalogs.unnamed_space");
  }

  function getRecordDescription(record: any): string {
    const description = record.attributes?.description;

    if (description) {
      return (
        description[$locale ?? ""] ||
        description.en ||
        description.ar ||
        $_("catalogs.no_description")
      );
    }

    if (
      record.resource_type === "ticket" &&
      record.attributes?.payload?.body?.content
    ) {
      const contentType = record.attributes?.payload?.content_type;

      if (contentType === "json") {
        let processedContent = record.attributes.payload.body.content
          .replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, "[Image: $1]")
          .replace(/<img[^>]*>/gi, "[Image]")
          .replace(/&nbsp;/g, " ")
          .replace(/&amp;/g, "&")
          .replace(/&lt;/g, "<")
          .replace(/&gt;/g, ">")
          .replace(/&quot;/g, '"')
          .replace(/&#39;/g, "'")
          .replace(/<[^>]*>/g, "")
          .replace(/\s+/g, " ")
          .trim();

        return processedContent.length > 200
          ? processedContent.substring(0, 200) + "..."
          : processedContent || $_("catalogs.no_description");
      } else {
        const textContent = record.attributes.payload.body.content.replace(
          /<[^>]*>/g,
          "",
        );
        return textContent.length > 200
          ? textContent.substring(0, 200) + "..."
          : textContent;
      }
    }

    if (record.attributes?.payload?.body) {
      const htmlContent = record.attributes.payload.body;
      if (typeof htmlContent === "string") {
        const contentType = record.attributes?.payload?.content_type;

        if (contentType === "json") {
          let processedContent = htmlContent
            .replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, "[Image: $1]")
            .replace(/<img[^>]*>/gi, "[Image]")
            .replace(/&nbsp;/g, " ")
            .replace(/&amp;/g, "&")
            .replace(/&lt;/g, "<")
            .replace(/&gt;/g, ">")
            .replace(/&quot;/g, '"')
            .replace(/&#39;/g, "'")
            .replace(/<[^>]*>/g, "")
            .replace(/\s+/g, " ")
            .trim();

          return processedContent.length > 200
            ? processedContent.substring(0, 200) + "..."
            : processedContent || $_("catalogs.no_description");
        } else {
          const textContent = htmlContent.replace(/<[^>]*>/g, "");
          return textContent.length > 200
            ? textContent.substring(0, 200) + "..."
            : textContent;
        }
      }
    }

    return $_("catalogs.no_description");
  }

  function formatDate(dateString: string): string {
    if (!dateString) return $_("common.not_available");
    return new Date(dateString).toLocaleDateString($locale ?? "", {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  }

  async function performSearch(query: string) {
    if (!query.trim()) {
      searchResults = [];
      filteredSpaces = spaces;
      return;
    }

    isSearching = true;
    try {
      const results = await searchInCatalog(query.trim(), 20);
      searchResults = results;

      filteredSpaces = [];
    } catch (err) {
      console.error("Error performing search:", err);
      error = $_("catalogs.error.search_failed");
      searchResults = [];
    } finally {
      isSearching = false;
    }
  }

  function applyFilters() {
    if (searchQuery.trim()) {
      return;
    }

    let filtered = spaces;

    if (filterActive !== "all") {
      filtered = filtered.filter((space: any) =>
        filterActive === "active"
          ? space.attributes?.is_active
          : !space.attributes?.is_active,
      );
    }

    filtered.sort((a: any, b: any) => {
      let result;
      switch (sortBy) {
        case "created":
          result = new Date(b.attributes?.created_at || 0).getTime() -
            new Date(a.attributes?.created_at || 0).getTime();
          break;
        case "updated":
          result = new Date(b.attributes?.updated_at || 0).getTime() -
            new Date(a.attributes?.updated_at || 0).getTime();
          break;
        default:
          result = getDisplayName(a).localeCompare(getDisplayName(b));
      }
      return sortOrder === "desc" ? -result : result;
    });

    filteredSpaces = filtered;
  }

  function toggleSortOrder() {
    sortOrder = sortOrder === "asc" ? "desc" : "asc";
    applyFilters();
  }

  function handleSearchInput() {
    if (searchTimeout) {
      clearTimeout(searchTimeout);
    }

    searchTimeout = setTimeout(() => {
      performSearch(searchQuery);
    }, 500);
  }

  // function handleContactUs() {
  //   $goto("/contact");
  // }

  $effect(() => {
    if (!searchQuery.trim()) {
      searchResults = [];
      applyFilters();
    }
  });

  $effect(() => {
    applyFilters();
  });

  // Color palette for space card avatars
  const avatarColors = [
    "#6366f1",
    "#8b5cf6",
    "#ec4899",
    "#f43f5e",
    "#f97316",
    "#eab308",
    "#22c55e",
    "#14b8a6",
    "#06b6d4",
    "#3b82f6",
    "#6d28d9",
    "#db2777",
  ];

  function getAvatarColor(index: number): string {
    return avatarColors[index % avatarColors.length];
  }
</script>

<div class="catalog-page" class:rtl={$isRTL}>
  <!-- Hero Section -->
  <section class="hero-section">
    <div class="hero-content">
      <div class="discover-badge">
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
        >
          <circle cx="12" cy="12" r="10"></circle>
          <path
            d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10"
          ></path>
        </svg>
        {$_("catalogs.discover")}
      </div>
      <h1 class="hero-title">
        {$_("catalogs.hero.title_explore")}
        <span class="hero-title-accent">{$_("catalogs.hero.title_spaces")}</span
        >
      </h1>
      <p class="hero-subtitle">{$_("catalogs.hero.subtitle")}</p>

      <div class="stats-row">
        <!-- Active Spaces -->
        <div class="stat-card">
          <div class="stat-icon stat-icon-spaces">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5"></path>
            </svg>
          </div>
          <div>
            {#if isLoading}
              <div class="skeleton skeleton-value"></div>
              <div class="skeleton skeleton-label"></div>
            {:else}
              <div class="stat-value">{formatNumber(spaces.length, $locale ?? "")}</div>
              <div class="stat-label">{$_("catalogs.stats.active_spaces")}</div>
            {/if}
          </div>
        </div>

        <!-- Users -->
        <div class="stat-card">
          <div class="stat-icon stat-icon-members">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"></path>
              <circle cx="9" cy="7" r="4"></circle>
              <path d="M23 21v-2a4 4 0 0 0-3-3.87"></path>
              <path d="M16 3.13a4 4 0 0 1 0 7.75"></path>
            </svg>
          </div>
          <div>
            {#if isStatsLoading}
              <div class="skeleton skeleton-value"></div>
              <div class="skeleton skeleton-label"></div>
            {:else}
              <div class="stat-value">{formatNumber(totalUsers, $locale ?? "")}</div>
              <div class="stat-label">{$_("catalogs.stats.members")}</div>
            {/if}
          </div>
        </div>

        <!-- All Entries -->
        <div class="stat-card">
          <div class="stat-icon stat-icon-posts">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"></polygon>
            </svg>
          </div>
          <div>
            {#if isStatsLoading}
              <div class="skeleton skeleton-value"></div>
              <div class="skeleton skeleton-label"></div>
            {:else}
              <div class="stat-value">{formatNumber(totalSpaceItems, $locale ?? "")}</div>
              <div class="stat-label">{$_("catalogs.stats.posts")}</div>
            {/if}
          </div>
        </div>
      </div>
    </div>
  </section>

  <!-- Search & Filters Section -->
  <section class="search-section">
    <div class="search-container">
      <!-- Compact search row: search + sort + expand button -->
      <div class="search-compact-row">
        <div class="search-bar">
          <svg
            class="search-icon"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
            ></path>
          </svg>
          <label for="search-input"></label>
          <input
            id="search-input"
            type="text"
            placeholder={$_("catalogs.search.placeholder")}
            bind:value={searchQuery}
            oninput={handleSearchInput}
            class="search-input"
          />
          {#if isSearching}
            <div class="search-loading">
              <div class="spinner spinner-sm"></div>
            </div>
          {/if}
          {#if searchQuery}
            <button
              class="clear-search-inline"
              onclick={() => {
                searchQuery = "";
                searchResults = [];
                applyFilters();
              }}
              aria-label={$_("catalogs.search.clear")}
            >
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"></path>
              </svg>
            </button>
          {/if}
        </div>

        <div class="sort-inline">
          <select
            bind:value={sortBy}
            class="filter-select sort-select"
            onchange={() => applyFilters()}
            title={$_("catalog_contents.filters.sort_by")}
            aria-label={$_("catalog_contents.filters.sort_by")}
          >
            <option value="name">{$_("catalogs.filter.name")}</option>
            <option value="created">{$_("catalogs.filter.newest")}</option>
            <option value="updated">{$_("catalogs.filter.popular")}</option>
          </select>
          <button
            onclick={toggleSortOrder}
            class="sort-order-button"
            title={$_("catalog_contents.filters.toggle_sort")}
            aria-label={$_("catalog_contents.filters.toggle_sort")}
          >
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              {#if sortOrder === "asc"}
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M3 4h13M3 8h9m-9 4h6m4 0l4-4m0 0l4 4m-4-4v12"></path>
              {:else}
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M3 4h13M3 8h9m-9 4h9m5-4v12m0 0l-4-4m4 4l-4-4"></path>
              {/if}
            </svg>
          </button>
        </div>

        <button
          onclick={() => (showFilters = !showFilters)}
          class="expand-filters-button"
          class:filters-active={showFilters || filterCategory !== "all" || filterTags !== "all" || filterActive !== "all"}
          title={showFilters ? $_("catalog_contents.filters.collapse_filters") : $_("catalog_contents.filters.expand_filters")}
          aria-label={showFilters ? $_("catalog_contents.filters.collapse_filters") : $_("catalog_contents.filters.expand_filters")}
        >
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z"></path>
          </svg>
          <svg class="expand-chevron" class:expand-chevron-open={showFilters} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7"></path>
          </svg>
        </button>
      </div>

      <!-- Collapsible filters panel -->
      {#if showFilters}
        <div class="collapsible-filters">
          <div class="filter-group">
            <span class="filter-label">{$_("catalogs.filter.category")}</span>
            <select
              bind:value={filterCategory}
              class="filter-select"
              onchange={() => applyFilters()}
              title={$_("catalogs.filter.category")}
              aria-label={$_("catalogs.filter.category")}
            >
              <option value="all">{$_("catalogs.filter.all")}</option>
            </select>
          </div>
          <div class="filter-group">
            <span class="filter-label">{$_("catalogs.filter.tags")}</span>
            <select
              bind:value={filterTags}
              class="filter-select"
              onchange={() => applyFilters()}
              title={$_("catalogs.filter.tags")}
              aria-label={$_("catalogs.filter.tags")}
            >
              <option value="all">{$_("catalogs.filter.all_tags")}</option>
            </select>
          </div>
          <div class="filter-group">
            <span class="filter-label">{$_("catalog_contents.filters.status")}</span>
            <select
              bind:value={filterActive}
              class="filter-select"
              onchange={() => applyFilters()}
              title={$_("catalog_contents.filters.status")}
              aria-label={$_("catalog_contents.filters.status")}
            >
              <option value="all">{$_("catalog_contents.filters.all_statuses")}</option>
              <option value="active">{$_("catalog_contents.filters.active")}</option>
              <option value="inactive">{$_("catalog_contents.filters.inactive")}</option>
            </select>
          </div>
        </div>
      {/if}
    </div>
  </section>

  <!-- Content Section -->
  <section class="content-section">
    {#if isLoading}
      <!-- Skeleton cards while spaces load -->
      <div class="spaces-grid">
        {#each Array(4) as _, i}
          <div class="space-card skeleton-card" style="animation-delay: {i * 80}ms">
            <div class="card-thumbnail skeleton-thumb"></div>
            <div class="card-body">
              <div class="skeleton skeleton-title"></div>
              <div class="skeleton skeleton-text"></div>
              <div class="skeleton skeleton-text-short"></div>
              <div class="card-footer-meta" style="margin-top: auto;">
                <div class="skeleton skeleton-meta"></div>
                <div class="skeleton skeleton-meta"></div>
              </div>
            </div>
          </div>
        {/each}
      </div>
    {:else if error}
      <div class="error-state">
        <div class="error-icon-wrap">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            ></path>
          </svg>
        </div>
        <h2 class="error-title">{$_("catalogs.error.title")}</h2>
        <p class="error-message">{error}</p>
      </div>
    {:else if searchQuery.trim() && searchResults.length === 0 && !isSearching}
      <div class="empty-state">
        <div class="empty-icon-wrap">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
            ></path>
          </svg>
        </div>
        <h3 class="empty-title">{$_("catalogs.empty.no_results_title")}</h3>
        <p class="empty-message">
          {$_("catalogs.empty.no_results_description")}
        </p>
      </div>
    {:else if searchQuery.trim() && searchResults.length > 0}
      <!-- Search Results -->
      <div class="search-results">
        <div class="results-header">
          <div class="results-info">
            <h2 class="results-title">
              {$_("catalogs.search.results_title")}
            </h2>
            <p class="results-subtitle">
              {$_("catalogs.search.results_subtitle")}
            </p>
          </div>
          <button
            class="clear-search-btn"
            onclick={() => {
              searchQuery = "";
              searchResults = [];
              applyFilters();
            }}
            aria-label={$_("catalogs.search.clear")}
          >
            <svg
              class="clear-icon"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M6 18L18 6M6 6l12 12"
              ></path>
            </svg>
            {$_("catalogs.search.clear")}
          </button>
        </div>

        <div class="spaces-grid">
          {#each searchResults as record, index}
            <!-- svelte-ignore a11y_no_noninteractive_element_to_interactive_role -->
            <article
              class="space-card"
              onclick={() => handleRecordClick(record)}
              role="button"
              tabindex="0"
              onkeydown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  handleRecordClick(record);
                }
              }}
              style="animation-delay: {index * 60}ms"
            >
              <div
                class="card-thumbnail"
                style="background: {getAvatarColor(index)}"
              >
                <span class="card-thumb-icon">
                  {record.shortname
                    ? record.shortname.charAt(0).toUpperCase()
                    : "R"}
                </span>
              </div>
              <div class="card-body">
                <div class="card-top-row">
                  <div>
                    <h3 class="card-title">{getRecordDisplayName(record)}</h3>
                    <p class="card-author">
                      {$_("catalogs.by")}
                      {record.attributes?.owner_shortname ||
                        $_("common.unknown")}
                    </p>
                  </div>
                </div>
                <p class="card-description">{getRecordDescription(record)}</p>
                {#if record.attributes?.tags && record.attributes.tags.length > 0}
                  <div class="card-tags">
                    {#each record.attributes.tags.slice(0, 3) as tag}
                      <span class="tag-pill">{tag}</span>
                    {/each}
                    {#if record.attributes.tags.length > 3}
                      <span class="tag-pill tag-more"
                        >+{record.attributes.tags.length - 3}</span
                      >
                    {/if}
                  </div>
                {/if}
                <div class="card-footer-meta">
                  <span class="card-date">
                    <svg
                      width="14"
                      height="14"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                    >
                      <rect x="3" y="4" width="18" height="18" rx="2" ry="2"
                      ></rect>
                      <line x1="16" y1="2" x2="16" y2="6"></line>
                      <line x1="8" y1="2" x2="8" y2="6"></line>
                      <line x1="3" y1="10" x2="21" y2="10"></line>
                    </svg>
                    {formatDate(record.attributes?.created_at)}
                  </span>
                </div>
              </div>
              <svg
                class="card-arrow"
                width="20"
                height="20"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
              >
                <path d="M9 18l6-6-6-6"></path>
              </svg>
            </article>
          {/each}
        </div>
      </div>
    {:else if !searchQuery.trim() && filteredSpaces.length === 0}
      <div class="empty-state">
        <div class="empty-icon-wrap">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"
            ></path>
          </svg>
        </div>
        <h3 class="empty-title">{$_("catalogs.empty.no_catalogs_title")}</h3>
        <p class="empty-message">
          {$_("catalogs.empty.no_catalogs_description")}
        </p>
      </div>
    {:else}
      <!-- Showing count + Live -->
      <div class="showing-bar">
        <span class="showing-count">
          {$_("catalogs.showing_spaces", {
            values: {
              count: formatNumberInText(filteredSpaces.length, $locale ?? ""),
            },
          })}
        </span>
        <span class="live-badge">
          <span class="live-dot"></span>
          {$_("catalogs.live")}
        </span>
      </div>

      <!-- Space Cards Grid -->
      <div class="spaces-grid">
        {#each filteredSpaces as space, index}
          <div
            class="space-card"
            onclick={() => handleSpaceClick(space)}
            role="button"
            tabindex="0"
            onkeydown={(e) => {
              if (e.key === "Enter" || e.key === " ") {
                handleSpaceClick(space);
              }
            }}
            style="animation-delay: {index * 60}ms"
          >
            <div
              class="card-thumbnail"
              style="background: {getAvatarColor(index)}"
            >
              <span class="card-thumb-icon">
                {space.shortname
                  ? space.shortname.charAt(0).toUpperCase()
                  : "S"}
              </span>
            </div>
            <div class="card-body">
              <div class="card-top-row">
                <div>
                  <h3 class="card-title">{getDisplayName(space)}</h3>
                  <p class="card-author">
                    {$_("catalogs.by")}
                    {space.attributes?.owner_shortname || $_("common.unknown")}
                  </p>
                </div>
                {#if isStatsLoading}
                  <span class="card-members-badge">
                    <div class="skeleton skeleton-badge"></div>
                  </span>
                {:else}
                  <span class="card-members-badge">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                      <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"></path>
                      <circle cx="9" cy="7" r="4"></circle>
                    </svg>
                    {formatNumber(getSpaceStats(space.shortname), $locale ?? "")}
                  </span>
                {/if}
              </div>
              <p class="card-description">{getDescription(space)}</p>

              {#if isStatsLoading}
                <div class="card-tags">
                  <div class="skeleton skeleton-tag"></div>
                  <div class="skeleton skeleton-tag"></div>
                </div>
              {:else if getTagsSpaces(space.shortname).length > 0}
                <div class="card-tags">
                  {#each getTagsSpaces(space.shortname).slice(0, 3) as tag}
                    <span class="tag-pill">{tag.name}</span>
                  {/each}
                  {#if getTagsSpaces(space.shortname).length > 3}
                    <span class="tag-pill tag-more"
                      >+{getTagsSpaces(space.shortname).length - 3}</span
                    >
                  {/if}
                </div>
              {/if}

              <div class="card-footer-meta">
                <span class="card-date">
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <path
                      d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z"
                    ></path>
                  </svg>
                  {space.attributes?.owner_shortname || $_("common.unknown")}
                </span>
                <span class="card-comments">
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <path
                      d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"
                    ></path>
                  </svg>
                  {formatNumber(getSpaceStats(space.shortname), $locale ?? "")}
                </span>
              </div>
            </div>
            <svg
              class="card-arrow"
              width="20"
              height="20"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
            >
              <path d="M9 18l6-6-6-6"></path>
            </svg>
          </div>
        {/each}
      </div>
    {/if}
  </section>
</div>

<style>
  .catalog-page {
    min-height: 100vh;
    background: var(--surface-page);
  }

  .rtl { direction: rtl; }

  /* ── Hero ── */
  .hero-section {
    padding: 2.5rem var(--space-page-x) 2rem;
    background: var(--gradient-page);
  }

  .hero-content {
    max-width: 72rem;
    margin: 0 auto;
  }

  .discover-badge {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.25rem 0.75rem;
    background: var(--color-primary-50);
    color: var(--color-success);
    border-radius: var(--radius-full);
    font-size: 0.6875rem;
    font-weight: 600;
    letter-spacing: 0.06em;
    margin-bottom: 0.875rem;
    border: 1px solid var(--color-primary-200);
  }

  .hero-title {
    font-size: clamp(1.75rem, 3.5vw, 2.75rem);
    font-weight: 800;
    color: var(--color-gray-900);
    line-height: 1.15;
    margin-bottom: 0.625rem;
    letter-spacing: -0.025em;
  }

  .hero-title-accent {
    color: var(--color-accent-600);
    font-style: italic;
  }

  .hero-subtitle {
    font-size: 1rem;
    color: var(--color-gray-500);
    line-height: 1.6;
    max-width: 34rem;
    margin-bottom: 1.75rem;
  }

  .stats-row {
    display: flex;
    gap: 0.75rem;
    flex-wrap: wrap;
  }

  .stat-card {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    padding: 0.75rem 1.25rem;
    background: var(--surface-card);
    border-radius: var(--radius-xl);
    border: 1px solid var(--color-gray-100);
    box-shadow: var(--shadow-xs);
    min-width: 140px;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .stat-card:hover {
    box-shadow: var(--shadow-md);
    border-color: var(--color-primary-200);
    transform: translateY(-1px);
  }

  .stat-icon {
    width: 2.25rem;
    height: 2.25rem;
    border-radius: var(--radius-lg);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .stat-icon-spaces { background: var(--color-primary-50); color: var(--color-accent-600); }
  .stat-icon-members { background: var(--color-primary-50); color: var(--color-error); }
  .stat-icon-posts { background: var(--color-primary-50); color: var(--color-info); }

  .stat-value {
    font-size: 1.375rem;
    font-weight: 700;
    color: var(--color-gray-900);
    line-height: 1;
  }

  .stat-label {
    font-size: 0.6875rem;
    color: var(--color-gray-400);
    font-weight: 500;
    margin-top: 0.125rem;
  }

  /* ── Search & Filters ── */
  .search-section {
    padding: 1rem var(--space-page-x);
    border-bottom: 1px solid var(--color-gray-100);
    background: var(--surface-card);
  }

  .search-container {
    max-width: 72rem;
    margin: 0 auto;
  }

  .search-compact-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .search-bar {
    position: relative;
    flex: 1;
    min-width: 0;
  }

  .search-icon {
    position: absolute;
    top: 50%;
    transform: translateY(-50%);
    width: 1.125rem;
    height: 1.125rem;
    color: var(--color-gray-400);
    left: 1rem;
  }

  .rtl .search-icon { left: auto; right: 1rem; }

  .search-input {
    width: 100%;
    padding: 0.625rem 1rem 0.625rem 2.75rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-xl);
    font-size: 0.875rem;
    background: var(--color-gray-50);
    transition: border-color var(--duration-normal) var(--ease-out),
                box-shadow var(--duration-normal) var(--ease-out),
                background var(--duration-normal) var(--ease-out);
    color: var(--color-gray-800);
  }

  .rtl .search-input { padding: 0.625rem 2.75rem 0.625rem 1rem; text-align: right; }

  .search-input:hover { border-color: var(--color-gray-300); }

  .search-input:focus {
    outline: none;
    border-color: var(--color-primary-400);
    background: var(--surface-card);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  .search-input::placeholder { color: var(--color-gray-400); }

  .search-loading {
    position: absolute;
    right: 2.5rem;
    top: 50%;
    transform: translateY(-50%);
  }

  .clear-search-inline {
    position: absolute;
    right: 0.75rem;
    top: 50%;
    transform: translateY(-50%);
    display: flex;
    align-items: center;
    padding: 0.25rem;
    border: none;
    background: none;
    color: var(--color-gray-400);
    cursor: pointer;
    border-radius: var(--radius-sm);
    transition: all var(--duration-fast) ease;
  }

  .clear-search-inline:hover {
    color: var(--color-gray-600);
    background: var(--color-gray-100);
  }

  .rtl .clear-search-inline { right: auto; left: 0.75rem; }

  .sort-inline {
    display: flex;
    gap: 0.375rem;
    flex-shrink: 0;
  }

  .sort-select {
    min-width: 0;
  }

  .sort-order-button {
    padding: 0.625rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    background: var(--surface-card);
    color: var(--color-gray-500);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
  }

  .sort-order-button:hover {
    border-color: var(--color-gray-300);
    color: var(--color-gray-700);
  }

  .sort-order-button:focus {
    outline: none;
    border-color: var(--color-primary-400);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  .expand-filters-button {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.625rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    background: var(--surface-card);
    color: var(--color-gray-500);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    flex-shrink: 0;
  }

  .expand-filters-button:hover {
    border-color: var(--color-gray-300);
    color: var(--color-gray-700);
  }

  .expand-filters-button.filters-active {
    color: var(--color-primary-600);
    border-color: var(--color-primary-300);
    background: var(--color-primary-50);
  }

  .expand-chevron {
    width: 0.875rem;
    height: 0.875rem;
    transition: transform var(--duration-normal) var(--ease-out);
  }

  .expand-chevron-open {
    transform: rotate(180deg);
  }

  .collapsible-filters {
    display: flex;
    flex-wrap: wrap;
    gap: 1rem;
    align-items: flex-end;
    padding-top: 0.75rem;
    margin-top: 0.75rem;
    border-top: 1px solid var(--color-gray-100);
  }

  .filter-group {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .filter-label {
    font-size: 0.6875rem;
    color: var(--color-gray-400);
    font-weight: 500;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .filter-select {
    padding: 0.4375rem 1.75rem 0.4375rem 0.625rem;
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-gray-700);
    background: var(--surface-card);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    appearance: auto;
  }

  .filter-select:hover { border-color: var(--color-gray-300); }

  .filter-select:focus {
    outline: none;
    border-color: var(--color-primary-400);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  /* ── Content Section ── */
  .content-section {
    padding: 1.5rem var(--space-page-x) 3rem;
    max-width: 72rem;
    margin: 0 auto;
  }

  /* ── Showing Bar ── */
  .showing-bar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1.25rem;
  }

  .showing-count {
    font-size: 0.8125rem;
    color: var(--color-gray-500);
    font-weight: 500;
  }

  .live-badge {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    font-size: 0.75rem;
    color: var(--color-success);
    font-weight: 600;
  }

  .live-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--color-success);
    animation: pulse-soft 2s ease-in-out infinite;
  }

  /* ── Skeleton Placeholders ── */
  .skeleton {
    background: linear-gradient(90deg, var(--color-gray-100) 25%, var(--color-gray-50) 50%, var(--color-gray-100) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s ease-in-out infinite;
    border-radius: var(--radius-sm);
  }

  .skeleton-value { width: 3rem; height: 1.375rem; border-radius: var(--radius-sm); margin-bottom: 0.25rem; }
  .skeleton-label { width: 5rem; height: 0.75rem; }
  .skeleton-title { width: 70%; height: 0.9375rem; margin-bottom: 0.5rem; }
  .skeleton-text { width: 100%; height: 0.75rem; margin-bottom: 0.375rem; }
  .skeleton-text-short { width: 55%; height: 0.75rem; }
  .skeleton-meta { width: 3.5rem; height: 0.625rem; }
  .skeleton-badge { width: 2rem; height: 0.625rem; }
  .skeleton-tag { width: 3rem; height: 1.125rem; border-radius: var(--radius-sm); }

  .skeleton-card {
    pointer-events: none;
  }

  .skeleton-thumb {
    background: linear-gradient(90deg, var(--color-gray-200) 25%, var(--color-gray-100) 50%, var(--color-gray-200) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s ease-in-out infinite;
  }

  .error-state, .empty-state {
    text-align: center;
    padding: 4rem 0;
  }

  .error-icon-wrap, .empty-icon-wrap {
    width: 3rem;
    height: 3rem;
    margin: 0 auto 1rem;
    color: var(--color-error);
  }

  .empty-icon-wrap { color: var(--color-gray-400); }

  .error-title, .empty-title {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--color-gray-900);
    margin-bottom: 0.375rem;
  }

  .error-message, .empty-message {
    color: var(--color-gray-500);
    font-size: 0.9375rem;
  }

  /* ── Search Results Header ── */
  .results-header {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    margin-bottom: 1.25rem;
    padding-bottom: 0.875rem;
    border-bottom: 1px solid var(--color-gray-100);
  }

  .results-info { flex: 1; }

  .results-title {
    font-size: 1.125rem;
    font-weight: 700;
    color: var(--color-gray-900);
    margin: 0 0 0.125rem 0;
  }

  .results-subtitle {
    color: var(--color-gray-500);
    font-size: 0.8125rem;
    margin: 0;
  }

  .clear-search-btn {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.375rem 0.875rem;
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    color: var(--color-gray-600);
    font-size: 0.75rem;
    font-weight: 500;
    cursor: pointer;
    transition: all var(--duration-fast) ease;
  }

  .clear-search-btn:hover { background: var(--color-gray-100); }

  .clear-icon { width: 0.8125rem; height: 0.8125rem; }

  /* ── Space Cards Grid ── */
  .spaces-grid {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 1rem;
  }

  .space-card {
    display: flex;
    flex-direction: column;
    background: var(--surface-card);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-xl);
    overflow: hidden;
    cursor: pointer;
    transition: transform var(--duration-slow) var(--ease-out),
                box-shadow var(--duration-slow) var(--ease-out),
                border-color var(--duration-slow) var(--ease-out);
    animation: fadeInUp 0.4s var(--ease-out) forwards;
    opacity: 0;
    position: relative;
  }

  .space-card:hover {
    border-color: var(--color-primary-200);
    box-shadow: var(--shadow-lg), 0 0 0 1px var(--color-primary-100);
    transform: translateY(-3px);
  }

  .space-card:active {
    transform: translateY(-1px);
    box-shadow: var(--shadow-sm);
  }

  .space-card:focus-visible {
    outline: 2px solid var(--color-primary-400);
    outline-offset: 2px;
  }

  .card-thumbnail {
    width: 100%;
    height: 80px;
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
  }

  .card-thumb-icon {
    font-size: 1.75rem;
    font-weight: 700;
    color: rgba(255, 255, 255, 0.9);
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  }

  .card-body {
    flex: 1;
    padding: 0.875rem 1rem;
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .card-top-row {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 0.625rem;
    margin-bottom: 0.375rem;
  }

  .card-title {
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-gray-900);
    margin: 0;
    line-height: 1.3;
  }

  .card-author {
    font-size: 0.6875rem;
    color: var(--color-gray-400);
    margin: 0.125rem 0 0 0;
    font-weight: 400;
  }

  .card-members-badge {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.125rem 0.5rem;
    background: var(--color-gray-50);
    border: 1px solid var(--color-gray-100);
    border-radius: var(--radius-full);
    font-size: 0.6875rem;
    font-weight: 600;
    color: var(--color-gray-500);
    white-space: nowrap;
    flex-shrink: 0;
  }

  .card-description {
    font-size: 0.8125rem;
    color: var(--color-gray-500);
    line-height: 1.5;
    margin: 0 0 0.5rem 0;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    flex: 1;
  }

  .card-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    margin-bottom: 0.5rem;
  }

  .tag-pill {
    padding: 0.125rem 0.4375rem;
    background: var(--color-primary-50);
    border: 1px solid var(--color-primary-100);
    border-radius: var(--radius-sm);
    font-size: 0.625rem;
    color: var(--color-primary-600);
    font-weight: 500;
    white-space: nowrap;
    transition: background var(--duration-fast) ease,
                border-color var(--duration-fast) ease,
                color var(--duration-fast) ease;
  }

  .space-card:hover .tag-pill {
    background: var(--color-primary-100);
    border-color: var(--color-primary-200);
    color: var(--color-primary-700);
  }

  .tag-pill.tag-more {
    background: var(--color-gray-100);
    border-color: var(--color-gray-200);
    color: var(--color-gray-500);
  }

  .card-footer-meta {
    display: flex;
    align-items: center;
    gap: 0.875rem;
    margin-top: auto;
  }

  .card-date, .card-comments {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.6875rem;
    color: var(--color-gray-400);
    font-weight: 400;
  }

  .card-arrow {
    position: absolute;
    right: 0.875rem;
    bottom: 0.875rem;
    color: var(--color-gray-300);
    transition: all var(--duration-normal) var(--ease-out);
  }

  .rtl .card-arrow { right: auto; left: 0.875rem; transform: rotate(180deg); }

  .space-card:hover .card-arrow {
    color: var(--color-primary-400);
    transform: translateX(3px);
  }

  .rtl .space-card:hover .card-arrow {
    transform: translateX(-3px) rotate(180deg);
  }

  /* ── Responsive ── */
  @media (min-width: 1440px) {
    .content-section { max-width: 88rem; }
    .spaces-grid { grid-template-columns: repeat(4, 1fr); }
  }

  @media (max-width: 1200px) {
    .spaces-grid { grid-template-columns: repeat(2, 1fr); }
  }

  @media (max-width: 768px) {
    .hero-section { padding: 1.5rem 1rem 1.25rem; }
    .search-section { padding: 1rem; }
    .content-section { padding: 1rem; }
    .hero-title { font-size: 1.5rem; }
    .hero-subtitle { font-size: 0.9375rem; }
    .stats-row { flex-direction: column; }
    .filter-group { flex-direction: row; align-items: center; gap: 0.5rem; }
    .card-thumbnail { height: 64px; }
    .spaces-grid { grid-template-columns: 1fr; gap: 0.75rem; }
    .showing-bar { flex-direction: column; gap: 0.375rem; align-items: flex-start; }
  }
</style>
