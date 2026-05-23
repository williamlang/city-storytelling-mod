// Lightweight YAML frontmatter parser for canon files. Splits a markdown
// document into a { fields, body } pair where fields is the leading
// `---...---` block parsed as flat `key: value` pairs, and body is
// everything after.
//
// Intentionally not a full YAML parser — only handles the shapes our
// template/ files actually use:
//   key: scalar value
//   key: [a, b, c]      ← kept as the literal "[a, b, c]" string
//
// Things we don't handle (and don't need yet):
//   - Multi-line `key: |` blocks
//   - Nested objects
//   - YAML anchors / refs
//
// Returns the original text as `body` and an empty fields object when
// the document has no frontmatter or the second `---` is missing.

export interface ParsedFrontmatter {
  fields: Record<string, string>;
  body: string;
}

const FRONTMATTER_RE = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/;

export function parseFrontmatter(text: string): ParsedFrontmatter {
  if (!text) return { fields: {}, body: text ?? "" };
  const match = FRONTMATTER_RE.exec(text);
  if (!match) return { fields: {}, body: text };

  const yaml = match[1];
  const body = match[2];
  const fields: Record<string, string> = {};
  for (const rawLine of yaml.split(/\r?\n/)) {
    const line = rawLine.trimEnd();
    // Skip blank lines and continuation lines (we don't handle multi-line
    // values; preserving them in the body would be confusing too).
    if (line.trim().length === 0) continue;
    if (line.startsWith(" ") || line.startsWith("\t")) continue;
    const colon = line.indexOf(":");
    if (colon < 0) continue;
    const key = line.slice(0, colon).trim();
    const value = line.slice(colon + 1).trim();
    if (key.length === 0) continue;
    fields[key] = value;
  }
  return { fields, body: body.trimStart() };
}
