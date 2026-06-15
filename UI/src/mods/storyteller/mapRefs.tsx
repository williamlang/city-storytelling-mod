import { ReactNode } from "react";
import styles from "./storyteller.module.scss";

// Surfaces the coordinate pairs the storyteller writes in prose — "(820, 1140)",
// "(-4000, -3500)" — as clickable jump targets that fly the in-game camera there
// (GH #29). The agent already emits these pairs organically from spatial data
// (carto/processed/*.md), so there's no new syntax to teach it.
//
// INLINE, NOT CHIPS (GH #44): these used to render out-of-prose as a row of
// block chips because Cohtml laid an element between text nodes out as a
// full-width block. The `cohinline` attribute (set on prose blocks by
// MarkdownLite, and on the chat row) fixes that — so the coordinate text itself
// is now the clickable target, flowing inline in the sentence. We render it as
// a plain text link (`.mapRefLink`), NOT a bordered pill: box decorations
// (border/background) on a `cohinline` *child* don't paint, so a pill would lose
// its border. A text link needs no box decoration and renders correctly.

export interface MapRef {
  x: number;
  y: number;
}

// Matches "(<int>, <int>)" with optional signs. Each number is either a
// plain 1–5 digit run OR a thousands-grouped form like "-1,500" / "-2,050" —
// the storyteller sometimes writes coordinates with a thousands separator,
// and without handling that the inner comma collides with the x/y delimiter
// and the pair never matches (the recentered frame spans a few thousand
// meters, so 1–5 digits / one thousands group covers every real coordinate).
// The thousands alternative is listed first so it wins when present; commas
// are stripped before parsing (see extractMapRefs).
const NUM = String.raw`-?\d{1,3}(?:,\d{3})+|-?\d{1,5}`;
const COORD_RE = new RegExp(`\\((${NUM}),\\s*(${NUM})\\)`, "g");

// Pulls every coordinate pair out of a block of text, de-duplicated and in
// first-seen order. Pure + DOM-free so it's unit-testable.
export function extractMapRefs(text: string): MapRef[] {
  const out: MapRef[] = [];
  if (!text) return out;
  COORD_RE.lastIndex = 0; // module-level /g regex — reset per call
  const seen = new Set<string>();
  let match: RegExpExecArray | null;
  while ((match = COORD_RE.exec(text)) !== null) {
    // Strip thousands separators before parsing — "-1,500" → -1500.
    const x = parseInt(match[1].replace(/,/g, ""), 10);
    const y = parseInt(match[2].replace(/,/g, ""), 10);
    const key = `${x},${y}`;
    if (!seen.has(key)) {
      seen.add(key);
      out.push({ x, y });
    }
  }
  return out;
}

// Linkifies coordinate pairs in a run of plain text: splits on the coordinate
// regex and returns the text as a list of nodes where each "(x, y)" becomes a
// clickable inline link (the literal coordinate text is preserved, so prose
// reads naturally) and everything else stays a bare string. Callers must place
// the result inside a `cohinline` block for the links to flow inline.
//
// `keyBase` namespaces the generated React keys so multiple calls within one
// parent (e.g. several text slices between markdown tokens) don't collide.
// When `onGoto` is omitted the text is returned unchanged (single string), so
// this is a no-op for contexts without a camera binding.
export function renderTextWithMapRefs(
  text: string,
  onGoto: ((x: number, y: number) => void) | undefined,
  keyBase: string
): ReactNode[] {
  if (!onGoto || !text) return [text];
  const out: ReactNode[] = [];
  COORD_RE.lastIndex = 0; // module-level /g regex — reset per call
  let last = 0;
  let n = 0;
  let match: RegExpExecArray | null;
  while ((match = COORD_RE.exec(text)) !== null) {
    if (match.index > last) out.push(text.slice(last, match.index));
    const x = parseInt(match[1].replace(/,/g, ""), 10);
    const y = parseInt(match[2].replace(/,/g, ""), 10);
    out.push(
      <a
        key={`${keyBase}-c${n++}`}
        className={styles.mapRefLink}
        title={`Fly the camera to (${x}, ${y})`}
        onClick={() => onGoto(x, y)}
      >
        {match[0]}
      </a>
    );
    last = COORD_RE.lastIndex;
  }
  if (last < text.length) out.push(text.slice(last));
  return out.length > 0 ? out : [text];
}
