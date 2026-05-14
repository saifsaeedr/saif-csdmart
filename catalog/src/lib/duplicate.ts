// Fields on a dmart record that the server owns and re-derives on create.
// Carrying them over to a duplicate would either be ignored or produce
// surprising clones (same uuid, stale timestamps, inherited relationships).
export const SERVER_MANAGED_FIELDS = [
  "uuid",
  "created_at",
  "updated_at",
  "slug",
  "owner_shortname",
  "relationships",
] as const;

export function stripServerManagedFields(
  attrs: Record<string, any> | null | undefined,
): Record<string, any> {
  if (!attrs) return {};
  const skip = new Set<string>(SERVER_MANAGED_FIELDS);
  const out: Record<string, any> = {};
  for (const key of Object.keys(attrs)) {
    if (!skip.has(key)) out[key] = attrs[key];
  }
  return out;
}
