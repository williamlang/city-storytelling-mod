import { useState } from "react";
import styles from "./storyteller.module.scss";
import type { CanonTree } from "./bindings";

// Sidebar canon browser. Each subdir (characters, places, etc.) is a
// collapsible group, all default-collapsed so a city with hundreds of
// files doesn't dump a wall of names on the player. Click a file →
// caller opens it in a new draggable modal (state lives in the parent
// StorytellerToolbar so closing the side panel doesn't dismiss open
// modals). Selection visuals show which files currently have a modal
// open.
//
// Empty state: when the tree has no subdirs yet (no city exported, or
// canon dirs are still empty), shows a nudge to run /new-city.
export function CanonBrowser({
  tree,
  openPaths,
  onOpen,
}: {
  tree: CanonTree;
  openPaths: string[];
  onOpen: (path: string) => void;
}) {
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const toggle = (sub: string) =>
    setExpanded((prev) => ({ ...prev, [sub]: !prev[sub] }));

  const subdirs = Object.keys(tree);
  const isEmpty = subdirs.length === 0;

  return (
    <aside className={styles.side}>
      <div className={styles.sideHeader}>Canon</div>
      <div className={styles.canonTree}>
        {isEmpty && (
          <div className={styles.canonEmpty}>
            No canon yet. Run <code>/new-city</code> to bootstrap.
          </div>
        )}
        {subdirs.map((sub) => {
          const open = !!expanded[sub];
          const entries = tree[sub];
          return (
            <div key={sub} className={styles.canonGroup}>
              <button
                type="button"
                className={styles.canonGroupHeader}
                onClick={() => toggle(sub)}
              >
                <span className={styles.canonGroupCaret}>{open ? "▼" : "▶"}</span>
                <span className={styles.canonGroupName}>{sub}</span>
                <span className={styles.canonGroupCount}>{entries.length}</span>
              </button>
              {open && (
                <div className={styles.canonGroupBody}>
                  {entries.map((entry) => (
                    <button
                      key={entry.path}
                      type="button"
                      className={`${styles.canonItem} ${
                        openPaths.includes(entry.path) ? styles.canonItemSelected : ""
                      }`}
                      onClick={() => onOpen(entry.path)}
                      title={entry.path}
                    >
                      {entry.name}
                    </button>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </aside>
  );
}
