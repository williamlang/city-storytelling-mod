import { describe, it, expect } from "vitest";
import { parseFrontmatter, extractMarkdownLinks } from "./frontmatter";

describe("parseFrontmatter", () => {
  it("extracts key:value pairs from a leading --- block", () => {
    const md = "---\nname: Annika\nrole: Mayor\n---\n\n## Background\n\nText.";
    const { fields, body } = parseFrontmatter(md);
    expect(fields).toEqual({ name: "Annika", role: "Mayor" });
    expect(body).toBe("## Background\n\nText.");
  });

  it("returns empty fields + full text as body when no frontmatter", () => {
    const md = "# Just markdown\n\nNo frontmatter here.";
    const { fields, body } = parseFrontmatter(md);
    expect(fields).toEqual({});
    expect(body).toBe(md);
  });

  it("returns empty fields + full text when closing fence is missing", () => {
    const md = "---\nname: Annika\n\nText that never closes.";
    const { fields, body } = parseFrontmatter(md);
    expect(fields).toEqual({});
    expect(body).toBe(md);
  });

  it("trims whitespace around keys and values", () => {
    // Leading indentation on a line is treated as a YAML continuation
    // and skipped (see the next test). For trimming, all keys must be
    // at column 0 — that's standard YAML for our use.
    const md = "---\nname  :   Annika  \nrole:Mayor\n---\nbody";
    const { fields } = parseFrontmatter(md);
    expect(fields.name).toBe("Annika");
    expect(fields.role).toBe("Mayor");
  });

  it("skips blank and indented (continuation) lines inside the frontmatter", () => {
    const md = "---\nname: Annika\n\nrole: Mayor\n  notes: indented should be skipped\nage: 51\n---\nbody";
    const { fields } = parseFrontmatter(md);
    expect(fields).toEqual({ name: "Annika", role: "Mayor", age: "51" });
    expect(fields.notes).toBeUndefined();
  });

  it("preserves list-shaped values as their literal string", () => {
    const md = "---\nallies: [marcus, riverside-ass]\nadversaries: []\n---\nbody";
    const { fields } = parseFrontmatter(md);
    expect(fields.allies).toBe("[marcus, riverside-ass]");
    expect(fields.adversaries).toBe("[]");
  });

  it("handles CRLF line endings", () => {
    const md = "---\r\nname: Annika\r\nrole: Mayor\r\n---\r\n\r\nbody";
    const { fields, body } = parseFrontmatter(md);
    expect(fields).toEqual({ name: "Annika", role: "Mayor" });
    expect(body).toBe("body");
  });

  it("returns empty for null or empty input", () => {
    expect(parseFrontmatter("")).toEqual({ fields: {}, body: "" });
    expect(parseFrontmatter(null as any)).toEqual({ fields: {}, body: "" });
  });

  it("doesn't treat --- in the body as a frontmatter close", () => {
    const md = "---\nname: Annika\n---\n\nFirst paragraph.\n\n---\n\nSecond paragraph.";
    const { fields, body } = parseFrontmatter(md);
    expect(fields).toEqual({ name: "Annika" });
    expect(body).toBe("First paragraph.\n\n---\n\nSecond paragraph.");
  });
});

describe("extractMarkdownLinks", () => {
  it("returns one entry per unique link in source order", () => {
    const body =
      "He met [Magnus](magnus.md) at the bar, then later " +
      "[Erik](erik.md), then back to [Magnus](magnus.md) again.";
    expect(extractMarkdownLinks(body)).toEqual([
      { text: "Magnus", href: "magnus.md" },
      { text: "Erik", href: "erik.md" },
    ]);
  });

  it("returns an empty array for body with no links", () => {
    expect(extractMarkdownLinks("Just prose, no links.")).toEqual([]);
  });

  it("handles relative paths with ../ segments", () => {
    const body = "See [Hayloft](../places/hayloft-steakhouse.md) for details.";
    expect(extractMarkdownLinks(body)).toEqual([
      { text: "Hayloft", href: "../places/hayloft-steakhouse.md" },
    ]);
  });

  it("includes external URLs (caller decides how to display them)", () => {
    const body = "See [docs](https://example.com/foo) for the spec.";
    expect(extractMarkdownLinks(body)).toEqual([
      { text: "docs", href: "https://example.com/foo" },
    ]);
  });

  it("returns empty for empty input", () => {
    expect(extractMarkdownLinks("")).toEqual([]);
    expect(extractMarkdownLinks(null as any)).toEqual([]);
  });
});
