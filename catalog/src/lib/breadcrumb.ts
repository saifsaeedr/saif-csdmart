export type BreadcrumbTarget =
  | { kind: "admin-root" }
  | { kind: "space-root"; spaceName: string }
  | { kind: "subpath"; spaceName: string; subpath: string };

// Parse a breadcrumb path string into a typed navigation target.
// Returns null for falsy input or paths that don't start with /dashboard/admin
// so the caller can no-op instead of navigating to a nonsensical URL.
export function parseBreadcrumbPath(path: unknown): BreadcrumbTarget | null {
  if (!path) return null;
  const segments = String(path).split("/").filter(Boolean);
  if (segments[0] !== "dashboard" || segments[1] !== "admin") return null;
  if (segments.length <= 2) return { kind: "admin-root" };
  if (segments.length === 3) {
    return { kind: "space-root", spaceName: segments[2] };
  }
  return {
    kind: "subpath",
    spaceName: segments[2],
    subpath: segments.slice(3).join("/"),
  };
}
