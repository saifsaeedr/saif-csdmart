import { describe, expect, it } from "vitest";
import { applyFolderContentDefaults } from "./folder_defaults";

describe("applyFolderContentDefaults", () => {
  it("fills every default when given an empty object", () => {
    const result = applyFolderContentDefaults({});
    expect(result.icon).toBe("");
    expect(result.icon_closed).toBe("");
    expect(result.icon_opened).toBe("");
    expect(result.shortname_title).toBe("");
    expect(result.index_attributes).toEqual([]);
    expect(result.query).toEqual({
      type: "",
      search: "",
      filter_types: [],
    });
    expect(result.search_columns).toEqual([]);
    expect(result.csv_columns).toEqual([]);
    expect(result.sort_by).toBe("");
    expect(result.sort_type).toBe("");
    expect(result.content_resource_types).toEqual([]);
    expect(result.content_schema_shortnames).toEqual([]);
    expect(result.workflow_shortnames).toEqual([]);
    expect(result.enable_pdf_schema_shortnames).toEqual([]);
    // The interesting defaults: view/create/update are true; the rest false.
    expect(result.allow_view).toBe(true);
    expect(result.allow_create).toBe(true);
    expect(result.allow_update).toBe(true);
    expect(result.allow_delete).toBe(false);
    expect(result.allow_create_category).toBe(false);
    expect(result.allow_csv).toBe(false);
    expect(result.allow_upload_csv).toBe(false);
    expect(result.use_media).toBe(false);
    expect(result.stream).toBe(false);
    expect(result.expand_children).toBe(false);
    expect(result.disable_filter).toBe(false);
  });

  it("handles null/undefined input by treating it as empty", () => {
    expect(applyFolderContentDefaults(null).allow_view).toBe(true);
    expect(applyFolderContentDefaults(undefined).allow_view).toBe(true);
  });

  it("preserves an explicit `false` from the server (regression for the `||` bug)", () => {
    // The previous implementation used `||`, which would silently turn
    // explicit `false` values back into `false` for fields whose default
    // was `false` (no-op) BUT into `true` for `allow_view`/`allow_create`/
    // `allow_update` whose default was `true`. The `??`-based merge must
    // keep an explicit `false` in all cases.
    const input = {
      allow_view: false,
      allow_create: false,
      allow_update: false,
      allow_delete: false,
      use_media: false,
    };
    const result = applyFolderContentDefaults(input);
    expect(result.allow_view).toBe(false);
    expect(result.allow_create).toBe(false);
    expect(result.allow_update).toBe(false);
    expect(result.allow_delete).toBe(false);
    expect(result.use_media).toBe(false);
  });

  it("preserves an explicit `true` from the server", () => {
    const input = {
      allow_delete: true,
      allow_csv: true,
      allow_upload_csv: true,
      use_media: true,
      stream: true,
      expand_children: true,
      disable_filter: true,
    };
    const result = applyFolderContentDefaults(input);
    expect(result.allow_delete).toBe(true);
    expect(result.allow_csv).toBe(true);
    expect(result.allow_upload_csv).toBe(true);
    expect(result.use_media).toBe(true);
    expect(result.stream).toBe(true);
    expect(result.expand_children).toBe(true);
    expect(result.disable_filter).toBe(true);
  });

  it("falls back to the default for `null` values (`??` semantics)", () => {
    // `null` is treated as missing for `??`, distinguishing this merge from
    // a naive `{...DEFAULTS, ...content}` which would let `null` override.
    const result = applyFolderContentDefaults({
      icon: null,
      allow_view: null,
      allow_delete: null,
    });
    expect(result.icon).toBe("");
    expect(result.allow_view).toBe(true);
    expect(result.allow_delete).toBe(false);
  });

  it("preserves caller-provided fields outside the known default set", () => {
    const input = {
      icon: "star",
      custom_field: "kept",
      nested: { x: 1 },
    };
    const result = applyFolderContentDefaults(input);
    expect(result.icon).toBe("star");
    expect(result.custom_field).toBe("kept");
    expect(result.nested).toEqual({ x: 1 });
  });

  it("preserves a caller-supplied query object verbatim", () => {
    const customQuery = {
      type: "search",
      search: "foo",
      filter_types: ["content"],
    };
    const result = applyFolderContentDefaults({ query: customQuery });
    expect(result.query).toEqual(customQuery);
  });
});
