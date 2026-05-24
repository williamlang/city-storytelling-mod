import { Fragment } from "react";
import styles from "./storyteller.module.scss";

// Minimal markdown renderer for canon files. Handles the shapes that
// template/ documents actually use:
//   # ## ### headers
//   **bold**
//   *italic* / _italic_
//   `inline code`
//   [link text](href)
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

export interface MarkdownLiteProps {
  children: string;
  // Called when a relative-path markdown link is clicked. Receives the
  // raw href as written in the markdown (e.g., "characters/annika.md").
  // External links (http://, https://) bypass this — they render as
  // styled anchors but don't navigate, since Coherent UI can't open
  // external URLs from inside the game.
  onLinkClick?: (href: string) => void;
}

export function MarkdownLite({ children, onLinkClick }: MarkdownLiteProps) {
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
      blocks.push(<Tag key={blocks.length}>{renderInline(headerMatch[2], onLinkClick)}</Tag>);
      i++;
      continue;
    }

    // Unordered list.
    if (/^\s*[-*]\s+/.test(line)) {
      const items: React.ReactNode[] = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
        const item = lines[i].replace(/^\s*[-*]\s+/, "");
        items.push(<li key={items.length}>{renderInline(item, onLinkClick)}</li>);
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
        items.push(<li key={items.length}>{renderInline(item, onLinkClick)}</li>);
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
        <blockquote key={blocks.length}>{renderInline(quoteLines.join(" "), onLinkClick)}</blockquote>
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
    blocks.push(<p key={blocks.length}>{renderInline(paraLines.join(" "), onLinkClick)}</p>);
  }

  return <>{blocks}</>;
}

// Inline formatting — link, bold, italic, code, applied left-to-right.
// Tokens are non-greedy; nested formatting (bold inside italic, link
// text containing bold, etc.) isn't supported because the template/
// files don't use it.
function renderInline(text: string, onLinkClick?: (href: string) => void): React.ReactNode {
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
      out.push(text.slice(lastIndex, match.index));
    }
    if (match[1]) out.push(renderLink(match[2], match[3], onLinkClick, key++));
    else if (match[4]) out.push(<strong key={key++}>{match[5]}</strong>);
    else if (match[6]) out.push(<em key={key++}>{match[7]}</em>);
    else if (match[8]) out.push(<em key={key++}>{match[9]}</em>);
    else if (match[10]) out.push(<code key={key++}>{match[11]}</code>);
    lastIndex = pattern.lastIndex;
  }
  if (lastIndex < text.length) out.push(text.slice(lastIndex));

  return out.length === 1 ? out[0] : <Fragment>{out}</Fragment>;
}

function renderLink(
  text: string,
  href: string,
  onLinkClick: ((href: string) => void) | undefined,
  key: number
): React.ReactNode {
  const isExternal = /^https?:\/\//i.test(href);
  if (isExternal) {
    // Coherent UI can't navigate to external URLs from inside the game,
    // so the link is presentational — shows the title attr so a curious
    // player can read the destination but clicks are no-ops.
    return (
      <a key={key} className={styles.canonLinkExternal} title={href}>
        {text}
      </a>
    );
  }
  // Internal link — relative path to another canon file. Click opens
  // another FileModal via the threaded onLinkClick callback.
  const handle = onLinkClick
    ? (e: React.MouseEvent) => {
        e.preventDefault();
        onLinkClick(href);
      }
    : undefined;
  return (
    <a
      key={key}
      className={styles.canonLink}
      title={href}
      onClick={handle}
      role="button"
    >
      {text}
    </a>
  );
}
