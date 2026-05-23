import { useMemo, useRef } from "react";
import styles from "./storyteller.module.scss";
import { useDrag } from "./useDrag";
import { parseFrontmatter } from "./frontmatter";
import { MarkdownLite } from "./MarkdownLite";
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
}: {
  entry: CanonEntry | undefined;
  path: string;
  cascadeIndex: number;
  onClose: () => void;
}) {
  const modalRef = useRef<HTMLDivElement | null>(null);
  const { pos, beginDrag } = useDrag();

  const onHeaderMouseDown = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest(`.${styles.fileModalClose}`)) return;
    beginDrag(e, modalRef.current);
  };

  // Initial cascade offset before the user has dragged.
  const offset = cascadeIndex * 24;
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
          <FileContent content={entry.content} />
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
function FileContent({ content }: { content: string }) {
  const { fields, body } = useMemo(() => parseFrontmatter(content), [content]);
  const fieldNames = Object.keys(fields);
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
      {body && <MarkdownLite>{body}</MarkdownLite>}
    </>
  );
}
