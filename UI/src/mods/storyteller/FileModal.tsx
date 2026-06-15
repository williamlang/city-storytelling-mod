import { useMemo, useRef } from "react";
import styles from "./storyteller.module.scss";
import { useDrag } from "./useDrag";
import { parseFrontmatter } from "./frontmatter";
import { MarkdownLite } from "./MarkdownLite";
import { mapGoto } from "./bindings";
import type { CanonEntry } from "./bindings";

// Single canon file rendered as a draggable modal. Multiple instances
// can coexist (one per open path); the parent StorytellerToolbar tracks
// which paths are open and renders one of these for each. cascadeIndex
// staggers the initial position so newly-opened modals don't all stack
// at the exact same spot.
export function FileModal({
  entry,
  path,
  cascadeIndex,
  onClose,
  onOpenFile,
}: {
  entry: CanonEntry | undefined;
  path: string;
  cascadeIndex: number;
  onClose: () => void;
  // Called when a markdown link inside this canon file is clicked.
  // Receives the href as written (cityDir-relative). Threading this
  // through lets one canon file link to another and have the click
  // open a new modal rather than dead-end.
  onOpenFile?: (path: string) => void;
}) {
  const modalRef = useRef<HTMLDivElement | null>(null);
  const { pos, beginDrag } = useDrag();

  const onHeaderMouseDown = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest(`.${styles.fileModalClose}`)) return;
    beginDrag(e, modalRef.current);
  };

  // Initial cascade offset before the user has dragged. The cascade
  // wraps after roughly half a dozen stacked modals so we don't run
  // straight off the right or bottom edge — for the unbounded case
  // see the modal's max-width / max-height CSS, which also caps the
  // already-on-screen modal so it doesn't clip content.
  const cascadeStep = 24;
  const cascadeWrap = 6;
  const wrappedIndex = cascadeIndex % cascadeWrap;
  const offset = wrappedIndex * cascadeStep;
  const style = pos
    ? { top: `${pos.y}px`, left: `${pos.x}px` }
    : { top: `${100 + offset}rem`, left: `${500 + offset}rem` };

  return (
    <div className={styles.fileModal} style={style} ref={modalRef}>
      <div className={styles.fileModalHeader} onMouseDown={onHeaderMouseDown}>
        <span className={styles.fileModalPath}>{path}</span>
        <button
          type="button"
          className={styles.fileModalClose}
          onClick={onClose}
        >
          ×
        </button>
      </div>
      <div className={styles.fileModalBody}>
        {entry ? (
          <FileContent content={entry.content} basePath={path} onOpenFile={onOpenFile} />
        ) : (
          <div className={styles.fileModalMissing}>
            File no longer in canon tree — it may have been deleted or renamed.
          </div>
        )}
      </div>
    </div>
  );
}

// Renders a canon file's content: frontmatter (if any) as a styled
// metadata block, then the body as markdown. Splitting the two halves
// stops the YAML `name: value` lines from collapsing into one run-on
// paragraph (markdown treats single newlines as whitespace) and
// surfaces the metadata as scannable structure. Internal to FileModal;
// not exported because no other component needs canon-file rendering
// (today).
function FileContent({
  content,
  basePath,
  onOpenFile,
}: {
  content: string;
  basePath: string;
  onOpenFile?: (path: string) => void;
}) {
  const { fields, body } = useMemo(() => parseFrontmatter(content), [content]);
  const fieldNames = Object.keys(fields);

  // Open an in-prose link: resolve its cityDir-relative href against this
  // file's directory and ask the parent to open a modal for it. External
  // links are ignored here (MarkdownLite doesn't wire them to a click).
  const onLinkClick = (href: string) => {
    if (!onOpenFile) return;
    onOpenFile(resolveCanonHref(basePath, href));
  };

  // Cross-reference links and coordinate pairs both render inline in the
  // markdown body now (GH #44) — the `cohinline` block attribute lets them
  // flow within the prose, so the old out-of-prose link list and coordinate
  // chip row are gone. Camera-jump on coordinate click goes straight to mapGoto.
  return (
    <>
      {fieldNames.length > 0 && (
        <dl className={styles.fileFrontmatter}>
          {fieldNames.map((k) => (
            <div key={k} className={styles.fileFrontmatterRow}>
              <dt className={styles.fileFrontmatterKey}>{k}</dt>
              <dd className={styles.fileFrontmatterValue}>{fields[k]}</dd>
            </div>
          ))}
        </dl>
      )}
      {body && (
        <div className={styles.fileMarkdownBody}>
          <MarkdownLite onLinkClick={onLinkClick} onMapGoto={mapGoto}>{body}</MarkdownLite>
        </div>
      )}
    </>
  );
}

// Resolve a markdown link's href against the directory of the file
// containing the link.
//   - "characters/foo.md"  (already has a slash) → returned unchanged.
//   - "foo.md"             (no slash, same dir)  → "<basedir>/foo.md".
//   - "../foo.md"          (parent dir)          → walk up one level.
//   - Absolute paths / URLs → returned unchanged.
export function resolveCanonHref(basePath: string, href: string): string {
  if (/^https?:\/\//i.test(href)) return href;
  if (href.startsWith("/")) return href.replace(/^\/+/, "");

  // Split basePath into directory segments (drop the filename).
  const baseSegs = basePath.split("/");
  baseSegs.pop();  // remove filename

  // Apply each segment of href, honoring ./ and ../
  const hrefSegs = href.split("/");
  for (const seg of hrefSegs) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") {
      if (baseSegs.length > 0) baseSegs.pop();
      continue;
    }
    baseSegs.push(seg);
  }
  return baseSegs.join("/");
}
