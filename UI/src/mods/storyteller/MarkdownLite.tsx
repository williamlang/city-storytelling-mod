import { Fragment } from "react";
import styles from "./storyteller.module.scss";
import { renderTextWithMapRefs } from "./mapRefs";

// Minimal markdown renderer for canon files. Handles the shapes that
// template/ documents actually use:
//   # ## ### headers
//   **bold**
//   *italic* / _italic_
//   `inline code`
//   [link text](href)   ← rendered as an inline <a>; see below
//   ```fenced code blocks```
//   - bullet lists
//   1. ordered lists
//   blank-line paragraph breaks
//   horizontal rules (---)
//
// Doesn't handle: nested lists, images, tables, blockquotes beyond
// simple `>` lines, HTML embeds. Adding any of those is fine — extend
// below — but we explicitly avoid react-markdown's remark/micromark
// dependency chain because it doesn't load cleanly in CS2's Coherent
// UI runtime (whole UI bundle fails to register).
//
// INLINE FLOW via `cohinline`. CS2's Coherent layout engine lays an
// element out *block-sized on its own line* whenever it sits between
// text nodes — so bold/italic/code/link spans in running prose used to
// drop onto their own full-width line (verified via CDP: a <strong> mid-
// paragraph computed width === the paragraph's full width). Gameface
// ships an official opt-in for exactly this — the `cohinline` attribute
// on the containing block — "for cases where flex layout fails to render
// styled text." We set it on every text-bearing block below; CDP confirms
// inline children then flow and wrap correctly (GH #44).
//   Docs: https://docs.coherent-labs.com/unity-gameface/content_development/inlinelayout/
//   Caveat: box decorations (background/border) on *child* elements are
//   unsupported under cohinline — only on the block itself. That's why the
//   inline links and coordinate jump targets below are plain TEXT links, not
//   bordered pills: a pill child would lose its border. (See mapRefs.tsx.)
//
// LINKS render as inline <a>. Pass `onLinkClick` to make them open the
// target (FileModal resolves the cityDir-relative href and opens a new
// modal); without it they render as styled-but-inert text.
//
// COORDINATE PAIRS — "(820, 1140)" — render as inline camera-jump links when
// `onMapGoto` is supplied (GH #44). They used to be out-of-prose chips for the
// same inline-flow reason links were; `cohinline` lets them flow inline now.

export interface MarkdownLiteProps {
  children: string;
  // Called with a link's raw (cityDir-relative or external) href when an
  // inline link is clicked. Omit to render links as inert styled text.
  onLinkClick?: (href: string) => void;
  // Called with map coordinates when an inline "(x, y)" pair is clicked.
  // Omit to leave coordinate pairs as plain text.
  onMapGoto?: (x: number, y: number) => void;
}

// Spread onto each text-bearing block to opt it into Cohtml inline layout.
// Typed loose because `cohinline` isn't in React's HTML attribute table —
// React still forwards unknown lowercase attributes to the DOM verbatim.
const cohinline = { cohinline: "" } as Record<string, string>;

export function MarkdownLite({ children, onLinkClick, onMapGoto }: MarkdownLiteProps) {
  const lines = (children ?? "").split(/\r?\n/);
  const blocks: React.ReactNode[] = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    // Fenced code block.
    if (/^```/.test(line)) {
      const codeLines: string[] = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) {
        codeLines.push(lines[i]);
        i++;
      }
      i++; // skip closing fence
      blocks.push(<pre key={blocks.length}><code>{codeLines.join("\n")}</code></pre>);
      continue;
    }

    // Horizontal rule.
    if (/^\s*-{3,}\s*$/.test(line)) {
      blocks.push(<hr key={blocks.length} />);
      i++;
      continue;
    }

    // Headers.
    const headerMatch = /^(#{1,6})\s+(.*)$/.exec(line);
    if (headerMatch) {
      const level = headerMatch[1].length;
      const Tag = `h${level}` as keyof JSX.IntrinsicElements;
      blocks.push(<Tag key={blocks.length} {...cohinline}>{renderInline(headerMatch[2], onLinkClick, onMapGoto)}</Tag>);
      i++;
      continue;
    }

    // Unordered list.
    if (/^\s*[-*]\s+/.test(line)) {
      const items: React.ReactNode[] = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
        const item = lines[i].replace(/^\s*[-*]\s+/, "");
        items.push(<li key={items.length} {...cohinline}>{renderInline(item, onLinkClick, onMapGoto)}</li>);
        i++;
      }
      blocks.push(<ul key={blocks.length}>{items}</ul>);
      continue;
    }

    // Ordered list.
    if (/^\s*\d+\.\s+/.test(line)) {
      const items: React.ReactNode[] = [];
      while (i < lines.length && /^\s*\d+\.\s+/.test(lines[i])) {
        const item = lines[i].replace(/^\s*\d+\.\s+/, "");
        items.push(<li key={items.length} {...cohinline}>{renderInline(item, onLinkClick, onMapGoto)}</li>);
        i++;
      }
      blocks.push(<ol key={blocks.length}>{items}</ol>);
      continue;
    }

    // Blockquote.
    if (/^\s*>\s?/.test(line)) {
      const quoteLines: string[] = [];
      while (i < lines.length && /^\s*>\s?/.test(lines[i])) {
        quoteLines.push(lines[i].replace(/^\s*>\s?/, ""));
        i++;
      }
      blocks.push(
        <blockquote key={blocks.length} {...cohinline}>{renderInline(quoteLines.join(" "), onLinkClick, onMapGoto)}</blockquote>
      );
      continue;
    }

    // Blank line — paragraph break.
    if (line.trim().length === 0) {
      i++;
      continue;
    }

    // Paragraph: consume contiguous non-blank, non-special lines.
    const paraLines: string[] = [line];
    i++;
    while (
      i < lines.length &&
      lines[i].trim().length > 0 &&
      !/^(#{1,6}\s|```|\s*[-*]\s|\s*\d+\.\s|>\s|---\s*$|---$)/.test(lines[i])
    ) {
      paraLines.push(lines[i]);
      i++;
    }
    blocks.push(<p key={blocks.length} {...cohinline}>{renderInline(paraLines.join(" "), onLinkClick, onMapGoto)}</p>);
  }

  return <>{blocks}</>;
}

// Inline formatting — link, bold, italic, code, applied left-to-right.
// Tokens are non-greedy; nested formatting isn't supported because the
// template/ files don't use it.
//
// Links render as inline <a>. When `onLinkClick` is supplied, a click
// reports the raw href to the caller (FileModal resolves + opens it);
// without it the link is styled-but-inert. Inline flow relies on the
// `cohinline` attribute set on the containing block above. Coordinate
// pairs in the plain-text runs between markdown tokens are linkified into
// inline camera-jump targets via renderTextWithMapRefs when `onMapGoto`
// is supplied (see mapRefs.tsx).
function renderInline(
  text: string,
  onLinkClick?: (href: string) => void,
  onMapGoto?: (x: number, y: number) => void
): React.ReactNode {
  // Combined tokenizer. Capture-group layout:
  //   1: full link    2: link text     3: link href
  //   4: full bold    5: bold text
  //   6: full *italic 7: italic text
  //   8: full _italic 9: italic text
  //  10: full code   11: code text
  const pattern =
    /(\[([^\]]+)\]\(([^)]+)\))|(\*\*([^*]+)\*\*)|(\*([^*]+)\*)|(_([^_]+)_)|(`([^`]+)`)/g;
  const out: React.ReactNode[] = [];
  let lastIndex = 0;
  let match: RegExpExecArray | null;
  let key = 0;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      for (const n of renderTextWithMapRefs(text.slice(lastIndex, match.index), onMapGoto, `t${key++}`)) {
        out.push(n);
      }
    }
    if (match[1]) {
      // Link: render an inline <a>. External (http/https) links are not
      // wired to a click — the FileModal link list is their affordance.
      const text = match[2];
      const href = match[3];
      const isExternal = /^https?:\/\//i.test(href);
      out.push(
        <a
          key={key++}
          className={styles.markdownLink}
          onClick={onLinkClick && !isExternal ? () => onLinkClick(href) : undefined}
        >
          {text}
        </a>
      );
    } else if (match[4]) out.push(<strong key={key++}>{match[5]}</strong>);
    else if (match[6]) out.push(<em key={key++}>{match[7]}</em>);
    else if (match[8]) out.push(<em key={key++}>{match[9]}</em>);
    else if (match[10]) out.push(<code key={key++}>{match[11]}</code>);
    lastIndex = pattern.lastIndex;
  }
  if (lastIndex < text.length) {
    for (const n of renderTextWithMapRefs(text.slice(lastIndex), onMapGoto, `t${key++}`)) {
      out.push(n);
    }
  }

  if (out.length === 0) return "";
  if (out.length === 1) return out[0];
  // Collapse adjacent strings into one — keeps the React child array
  // from being [string, string, string] which Coherent's reconciler
  // has occasionally mishandled. Inline elements (bold/em/code) stay
  // as their own entries.
  const collapsed: React.ReactNode[] = [];
  for (const node of out) {
    const last = collapsed[collapsed.length - 1];
    if (typeof node === "string" && typeof last === "string") {
      collapsed[collapsed.length - 1] = last + node;
    } else {
      collapsed.push(node);
    }
  }
  if (collapsed.length === 1) return collapsed[0];
  return <Fragment>{collapsed}</Fragment>;
}
