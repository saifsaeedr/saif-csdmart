namespace Dmart.Models.Core;

// Per-category row counts for a delete — or, on a dryrun, a projection of what a delete
// WOULD remove. A delete cascades across up to four tables; this captures each so a
// caller sees the full blast radius. Total is the `affected` number; ToObject() is the
// `report` breakdown surfaced in the response. Cascade repos build it by capturing each
// DELETE's affected-row count inside one transaction, so a dryrun (same statements, then
// ROLLBACK) reports exactly what a real delete (same statements, then COMMIT) removes.
public readonly record struct DeleteReport(long Entries, long Attachments, long Histories, long Locks)
{
    public static readonly DeleteReport Empty = new(0, 0, 0, 0);

    public long Total => Entries + Attachments + Histories + Locks;

    public DeleteReport Add(in DeleteReport o) => new(
        Entries + o.Entries, Attachments + o.Attachments, Histories + o.Histories, Locks + o.Locks);

    public Dictionary<string, object> ToObject() => new()
    {
        ["entries"] = Entries,
        ["attachments"] = Attachments,
        ["histories"] = Histories,
        ["locks"] = Locks,
    };
}
