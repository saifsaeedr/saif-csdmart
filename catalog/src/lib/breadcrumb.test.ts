import { describe, expect, it } from "vitest";
import { parseBreadcrumbPath } from "./breadcrumb";

describe("parseBreadcrumbPath", () => {
  it("returns null for falsy input", () => {
    expect(parseBreadcrumbPath(undefined)).toBeNull();
    expect(parseBreadcrumbPath(null)).toBeNull();
    expect(parseBreadcrumbPath("")).toBeNull();
    expect(parseBreadcrumbPath(0)).toBeNull();
  });

  it("rejects paths that don't start with /dashboard/admin", () => {
    expect(parseBreadcrumbPath("/")).toBeNull();
    expect(parseBreadcrumbPath("/catalogs")).toBeNull();
    expect(parseBreadcrumbPath("/dashboard")).toBeNull();
    expect(parseBreadcrumbPath("/dashboard/user/profile")).toBeNull();
    expect(parseBreadcrumbPath("/admin/dashboard")).toBeNull();
    expect(parseBreadcrumbPath("/some/random/path")).toBeNull();
  });

  it("parses the admin root", () => {
    expect(parseBreadcrumbPath("/dashboard/admin")).toEqual({
      kind: "admin-root",
    });
    expect(parseBreadcrumbPath("dashboard/admin")).toEqual({
      kind: "admin-root",
    });
    expect(parseBreadcrumbPath("/dashboard/admin/")).toEqual({
      kind: "admin-root",
    });
  });

  it("parses a space-root path", () => {
    expect(parseBreadcrumbPath("/dashboard/admin/applications")).toEqual({
      kind: "space-root",
      spaceName: "applications",
    });
  });

  it("parses a single-segment subpath", () => {
    expect(parseBreadcrumbPath("/dashboard/admin/applications/forms")).toEqual({
      kind: "subpath",
      spaceName: "applications",
      subpath: "forms",
    });
  });

  it("preserves multi-segment subpaths joined by '/'", () => {
    expect(
      parseBreadcrumbPath("/dashboard/admin/applications/forms/contact"),
    ).toEqual({
      kind: "subpath",
      spaceName: "applications",
      subpath: "forms/contact",
    });
  });

  it("tolerates non-string input via String() coercion", () => {
    // The svelte template can hand in arbitrary `any`; the helper coerces.
    expect(parseBreadcrumbPath(42)).toBeNull();
    expect(parseBreadcrumbPath({ toString: () => "/dashboard/admin/x" })).toEqual(
      { kind: "space-root", spaceName: "x" },
    );
  });
});
