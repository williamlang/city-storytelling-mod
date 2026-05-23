import { Fragment } from "react";

// Minimal markdown renderer for canon files. Handles the shapes that
// template/ documents actually use:
//   # ## ### headers
//   **bold**
//   *italic* / _italic_
//   `inline code`
//   ```fenced code blocks```
//   - bullet lists
//   1. ordered lists
//   blank-line paragraph breaks
//   horizontal rules (---)
//
// Doesn't handle: nested lists, links/images, tables, blockquotes
// beyond simple `>` lines, HTML embeds. Adding any of those is fine
// — extend below — but we explicitly avoid react-markdown's
// remark/micromark dependency chain because it doesn't load cleanly
// in CS2's Coherent UI runtime (whole UI bundle fails to register).

export function MarkdownLite({ children }: { children: string }) {
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
      blocks.push(<Tag key={blocks.length}>{renderInline(headerMatch[2])}</Tag>);
      i++;
      continue;
    }

    // Unordered list.
    if (/^\s*[-*]\s+/.test(line)) {
      const items: React.ReactNode[] = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) {
        const item = lines[i].replace(/^\s*[-*]\s+/, "");
        items.push(<li key={items.length}>{renderInline(item)}</li>);
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
        items.push(<li key={items.length}>{renderInline(item)}</li>);
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
        <blockquote key={blocks.length}>{renderInline(quoteLines.join(" "))}</blockquote>
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
    blocks.push(<p key={blocks.length}>{renderInline(paraLines.join(" "))}</p>);
  }

  return <>{blocks}</>;
}

// Inline formatting — bold/italic/code, applied in that order. Tokens
// are non-greedy; nested formatting (bold inside italic) isn't supported
// because the template/ files don't use it.
function renderInline(text: string): React.ReactNode {
  // Tokenize via a single combined regex; each match captures the
  // delimiter type and inner text. Everything between matches is
  // passed through as plain text.
  const pattern = /(\*\*([^*]+)\*\*)|(\*([^*]+)\*)|(_([^_]+)_)|(`([^`]+)`)/g;
  const out: React.ReactNode[] = [];
  let lastIndex = 0;
  let match: RegExpExecArray | null;
  let key = 0;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      out.push(text.slice(lastIndex, match.index));
    }
    if (match[1]) out.push(<strong key={key++}>{match[2]}</strong>);
    else if (match[3]) out.push(<em key={key++}>{match[4]}</em>);
    else if (match[5]) out.push(<em key={key++}>{match[6]}</em>);
    else if (match[7]) out.push(<code key={key++}>{match[8]}</code>);
    lastIndex = pattern.lastIndex;
  }
  if (lastIndex < text.length) out.push(text.slice(lastIndex));

  return out.length === 1 ? out[0] : <Fragment>{out}</Fragment>;
}
