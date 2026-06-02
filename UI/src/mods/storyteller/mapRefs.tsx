import styles from "./storyteller.module.scss";

// Surfaces the coordinate pairs the storyteller writes in prose — "(820, 1140)",
// "(-4000, -3500)" — as clickable chips that fly the in-game camera there
// (GH #29). The agent already emits these pairs organically from spatial data
// (carto/processed/*.md), so there's no new syntax to teach it.
//
// WHY CHIPS, NOT INLINE PINS: Cohtml (CS2's UI engine) cannot inline-flow a
// child *element* inside running text — any <span>/<a>/<svg>/etc. between text
// nodes is laid out as a full-width block on its own line, regardless of
// `display` (confirmed live: even stylesheet `display:inline !important` keeps
// it full-width). This is the same limitation that made FileModal surface
// cross-reference links as a separate clickable list rather than inline. So we
// leave the "(x, y)" text untouched in the prose (text nodes flow fine) and
// render the clickable jump targets out-of-prose as a row of block chips.

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

// Small map-pin drawn as inline SVG, not an emoji — Coherent's UI font has no
// color-emoji glyphs (📍 renders as a tofu box in-game). SVG renders reliably
// and fill is hard-coded because currentColor doesn't propagate into SVG under
// Cohtml. Lives inside a block chip, so its own display doesn't matter.
function PinIcon() {
  return (
    <svg
      className={styles.mapChipIcon}
      width="10"
      height="10"
      viewBox="0 0 24 24"
      xmlns="http://www.w3.org/2000/svg"
    >
      <path
        fill="#5bb3e6"
        d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5a2.5 2.5 0 110-5 2.5 2.5 0 010 5z"
      />
    </svg>
  );
}

// A wrap-row of clickable jump-to-coordinate chips, rendered beneath prose
// (chat messages, canon files). Returns null when the text mentions no
// coordinates, so callers can drop it in unconditionally.
export function MapRefChips({
  text,
  onGoto,
}: {
  text: string;
  onGoto: (x: number, y: number) => void;
}) {
  const refs = extractMapRefs(text);
  if (refs.length === 0) return null;
  return (
    <div className={styles.mapChips}>
      {refs.map((r) => (
        <button
          key={`${r.x},${r.y}`}
          type="button"
          className={styles.mapChip}
          title={`Fly the camera to (${r.x}, ${r.y})`}
          onClick={() => onGoto(r.x, r.y)}
        >
          <PinIcon />
          {r.x}, {r.y}
        </button>
      ))}
    </div>
  );
}
