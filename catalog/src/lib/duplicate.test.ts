import { describe, expect, it } from "vitest";
import {
  SERVER_MANAGED_FIELDS,
  stripServerManagedFields,
} from "./duplicate";

describe("stripServerManagedFields", () => {
  it("returns an empty object for null or undefined input", () => {
    expect(stripServerManagedFields(null)).toEqual({});
    expect(stripServerManagedFields(undefined)).toEqual({});
  });

  it("returns an empty object when input is empty", () => {
    expect(stripServerManagedFields({})).toEqual({});
  });

  it("drops every server-managed field", () => {
    const input = Object.fromEntries(
      SERVER_MANAGED_FIELDS.map((k) => [k, `value-for-${k}`]),
    );
    expect(stripServerManagedFields(input)).toEqual({});
  });

  it("preserves non-managed fields exactly", () => {
    const input = {
      uuid: "abc",
      displayname: { en: "Hello" },
      shortname: "hello",
      status: "draft",
      payload: { body: { x: 1 } },
    };
    expect(stripServerManagedFields(input)).toEqual({
      displayname: { en: "Hello" },
      shortname: "hello",
      status: "draft",
      payload: { body: { x: 1 } },
    });
  });

  it("preserves falsy non-managed values (false, 0, null, '')", () => {
    const input = {
      uuid: "u",
      is_active: false,
      count: 0,
      note: "",
      detail: null,
    };
    expect(stripServerManagedFields(input)).toEqual({
      is_active: false,
      count: 0,
      note: "",
      detail: null,
    });
  });

  it("does not mutate the input object", () => {
    const input = { uuid: "abc", shortname: "x" };
    const before = JSON.stringify(input);
    stripServerManagedFields(input);
    expect(JSON.stringify(input)).toBe(before);
  });

  it("strips relationships specifically (not carried into duplicates)", () => {
    const input = {
      shortname: "doc",
      relationships: [{ related_to: "other", related_type: "linked" }],
    };
    const result = stripServerManagedFields(input);
    expect(result.relationships).toBeUndefined();
    expect(result.shortname).toBe("doc");
  });
});
