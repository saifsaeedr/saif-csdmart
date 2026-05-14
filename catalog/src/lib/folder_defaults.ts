// Defaults applied to a folder's content when its metadata payload is loaded
// into the FolderForm. Uses `??` (nullish coalescing) per field so that an
// explicit `false` from the server is preserved — a previous version of this
// merge used `||`, which silently overwrote `false` values with the default
// (and never actually filled in `undefined` because the spread came after).
export function applyFolderContentDefaults(content: any): Record<string, any> {
  const c = content ?? {};
  return {
    ...c,
    icon: c.icon ?? "",
    icon_closed: c.icon_closed ?? "",
    icon_opened: c.icon_opened ?? "",
    shortname_title: c.shortname_title ?? "",

    index_attributes: c.index_attributes ?? [],

    query: c.query ?? {
      type: "",
      search: "",
      filter_types: [],
    },

    search_columns: c.search_columns ?? [],
    csv_columns: c.csv_columns ?? [],

    sort_by: c.sort_by ?? "",
    sort_type: c.sort_type ?? "",

    content_resource_types: c.content_resource_types ?? [],
    content_schema_shortnames: c.content_schema_shortnames ?? [],
    workflow_shortnames: c.workflow_shortnames ?? [],
    enable_pdf_schema_shortnames: c.enable_pdf_schema_shortnames ?? [],

    allow_view: c.allow_view ?? true,
    allow_create: c.allow_create ?? true,
    allow_update: c.allow_update ?? true,
    allow_delete: c.allow_delete ?? false,
    allow_create_category: c.allow_create_category ?? false,
    allow_csv: c.allow_csv ?? false,
    allow_upload_csv: c.allow_upload_csv ?? false,
    use_media: c.use_media ?? false,
    stream: c.stream ?? false,
    expand_children: c.expand_children ?? false,
    disable_filter: c.disable_filter ?? false,
  };
}
