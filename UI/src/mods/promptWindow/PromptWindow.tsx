import { useEffect, useMemo, useRef, useState } from "react";
import { Button } from "cs2/ui";
import { useValue } from "cs2/api";
import ReactMarkdown from "react-markdown";
import storytellerIcon from "../../assets/storyteller_icon.svg";
import styles from "./PromptWindow.module.scss";
import { useDrag } from "./useDrag";
import {
  messagesBinding,
  isRunningBinding,
  tokenSummaryBinding,
  lastErrorBinding,
  availableCommandsBinding,
  canonTreeBinding,
  submitPrompt,
  cancelRun,
  ChatMessage,
  SlashCommand,
  CanonTree,
  CanonEntry,
} from "./bindings";

// Top-level Storyteller panel. Toolbar icon (floating variant matches CS2's
// other top-left tool mods) toggles a draggable panel containing:
//   - chat history (user / assistant rows, scrollable)
//   - free-form prompt textarea
//   - command-picker dropdown + Run/Cancel
//   - side panel with a canon browser
//
// Canon files clicked from the sidebar open as separate draggable modals
// rendered alongside the main panel; multiple can be open at once. Modal
// state lives at this level so closing the main panel doesn't dismiss
// them (though that's a debatable UX — for now they stick around).
export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState("");
  const [commandMenuOpen, setCommandMenuOpen] = useState(false);
  const [openModals, setOpenModals] = useState<string[]>([]);

  const messagesJson = useValue(messagesBinding);
  const isRunning = useValue(isRunningBinding);
  const tokenSummary = useValue(tokenSummaryBinding);
  const lastError = useValue(lastErrorBinding);
  const commandsJson = useValue(availableCommandsBinding);
  const canonJson = useValue(canonTreeBinding);

  const messages = useMemo<ChatMessage[]>(() => {
    try { return JSON.parse(messagesJson); } catch { return []; }
  }, [messagesJson]);

  const commands = useMemo<SlashCommand[]>(() => {
    try { return JSON.parse(commandsJson); } catch { return []; }
  }, [commandsJson]);

  const canonTree = useMemo<CanonTree>(() => {
    try { return JSON.parse(canonJson); } catch { return {}; }
  }, [canonJson]);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages.length]);

  const panelRef = useRef<HTMLDivElement | null>(null);
  const { pos: panelPos, beginDrag } = useDrag();
  const onHeaderMouseDown = (e: React.MouseEvent) => {
    // Skip when the click is on the close button so its handler fires
    // without starting a drag.
    if ((e.target as HTMLElement).closest(`.${styles.close}`)) return;
    beginDrag(e, panelRef.current);
  };

  const canSubmit = draft.trim().length > 0 && !isRunning;
  const handleSubmit = () => {
    if (!canSubmit) return;
    submitPrompt(draft.trim());
    setDraft("");
  };
  const runCommand = (cmdName: string) => {
    setCommandMenuOpen(false);
    submitPrompt(`/${cmdName}`);
  };

  const openFile = (path: string) => {
    setOpenModals((prev) => (prev.includes(path) ? prev : [...prev, path]));
  };
  const closeFile = (path: string) => {
    setOpenModals((prev) => prev.filter((p) => p !== path));
  };

  // Flatten the tree once so FileModal can look up content by path without
  // re-walking the tree on each render.
  const flatCanon = useMemo<Record<string, CanonEntry>>(() => {
    const out: Record<string, CanonEntry> = {};
    for (const sub of Object.keys(canonTree)) {
      for (const e of canonTree[sub]) out[e.path] = e;
    }
    return out;
  }, [canonTree]);

  const panelStyle = panelPos
    ? { top: `${panelPos.y}px`, left: `${panelPos.x}px` }
    : undefined;

  return (
    <>
      <Button
        variant="floating"
        onClick={() => setOpen((v) => !v)}
        aria-label="Storyteller"
      >
        <img src={storytellerIcon} className={styles.toolbarIcon} alt="" />
      </Button>

      {open && (
        <div className={styles.panel} style={panelStyle} ref={panelRef}>
          <div className={styles.header} onMouseDown={onHeaderMouseDown}>
            <span className={styles.title}>Storyteller</span>
            <button
              type="button"
              className={styles.close}
              onClick={() => setOpen(false)}
            >
              ×
            </button>
          </div>

          <div className={styles.body}>
            <div className={styles.main}>
              <div className={styles.chat} ref={scrollRef}>
                {messages.length === 0 && (
                  <div className={styles.empty}>
                    Ask the storyteller to do something. Free-form prompts or pick
                    a command from the menu below.
                  </div>
                )}
                {messages.map((m, i) => (
                  <ChatRow key={i} msg={m} />
                ))}
                {lastError && (
                  <div className={`${styles.row} ${styles.errorRow}`}>
                    <span className={styles.role}>error</span>
                    <span className={styles.text}>{lastError}</span>
                  </div>
                )}
              </div>

              <textarea
                className={styles.prompt}
                placeholder={isRunning ? "Running…" : "Type a prompt…"}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    handleSubmit();
                  }
                }}
                rows={3}
                disabled={isRunning}
              />

              <div className={styles.footer}>
                <span className={styles.status}>
                  {tokenSummary || (isRunning ? "Running…" : "Idle")}
                </span>
                <div className={styles.actions}>
                  <CommandMenu
                    commands={commands}
                    open={commandMenuOpen}
                    disabled={isRunning}
                    onToggle={() => setCommandMenuOpen((v) => !v)}
                    onPick={runCommand}
                  />
                  {isRunning ? (
                    <button
                      type="button"
                      className={styles.cancel}
                      onClick={() => cancelRun()}
                    >
                      Cancel
                    </button>
                  ) : (
                    <button
                      type="button"
                      className={styles.run}
                      disabled={!canSubmit}
                      onClick={handleSubmit}
                    >
                      Run
                    </button>
                  )}
                </div>
              </div>
            </div>

            <CanonBrowser
              tree={canonTree}
              openPaths={openModals}
              onOpen={openFile}
            />
          </div>
        </div>
      )}

      {openModals.map((path, i) => (
        <FileModal
          key={path}
          entry={flatCanon[path]}
          path={path}
          cascadeIndex={i}
          onClose={() => closeFile(path)}
        />
      ))}
    </>
  );
}

function ChatRow({ msg }: { msg: ChatMessage }) {
  const roleClass = msg.role === "user" ? styles.userRow : styles.assistantRow;
  return (
    <div className={`${styles.row} ${roleClass}`}>
      <span className={styles.role}>{msg.role}</span>
      <div className={styles.body}>
        <div className={styles.text}>{msg.text}</div>
      </div>
    </div>
  );
}

// Canon browser. Each subdir (characters, places, etc.) is a collapsible
// group, all default-collapsed so a city with hundreds of files doesn't
// dump a wall of names on the player. Click a file → caller opens it in
// a new draggable modal. Selection visuals show which files currently
// have a modal open.
function CanonBrowser({
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

// Single canon file rendered as a draggable modal with markdown content.
// Multiple instances of this can coexist (one per open path). cascadeIndex
// staggers initial positions so newly-opened modals don't all stack at
// the exact same spot.
function FileModal({
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
      <div
        className={styles.fileModalHeader}
        onMouseDown={onHeaderMouseDown}
      >
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
          <ReactMarkdown>{entry.content}</ReactMarkdown>
        ) : (
          <div className={styles.fileModalMissing}>
            File no longer in canon tree — it may have been deleted or renamed.
          </div>
        )}
      </div>
    </div>
  );
}

// Custom dropdown — toggle button + absolute-positioned popup list. Each
// item shows the command name and its frontmatter description. Picking
// closes the menu and submits the command immediately (no Run click).
//
// Click-outside-to-close: document-level mousedown listener attaches only
// while the menu is open. We check whether the click target is inside the
// dropdown root before closing — clicks on the toggle bubble up here too,
// so this guard prevents the toggle from closing then re-opening on the
// same click.
function CommandMenu({
  commands,
  open,
  disabled,
  onToggle,
  onPick,
}: {
  commands: SlashCommand[];
  open: boolean;
  disabled: boolean;
  onToggle: () => void;
  onPick: (name: string) => void;
}) {
  const rootRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (rootRef.current && rootRef.current.contains(e.target as Node)) return;
      onToggle();
    };
    document.addEventListener("mousedown", onDocMouseDown);
    return () => document.removeEventListener("mousedown", onDocMouseDown);
  }, [open, onToggle]);

  if (commands.length === 0) return null;

  return (
    <div className={styles.commandMenuRoot} ref={rootRef}>
      <button
        type="button"
        className={styles.commandToggle}
        disabled={disabled}
        onClick={onToggle}
      >
        <span>Commands</span>
        {/* Inline SVG chevron — CS2's Coherent UI font doesn't ship the
            ▾ glyph (U+25BE), which renders as tofu / a tiny square. SVG
            guarantees the chevron actually paints. Stroke color is set
            explicitly because Coherent UI doesn't propagate `color` to
            SVG `currentColor` references in some builds. */}
        <svg
          viewBox="0 0 10 10"
          aria-hidden="true"
          className={styles.commandToggleChevron}
        >
          <path
            d="M2 4 L5 7 L8 4"
            stroke="#cfe5f5"
            strokeWidth="1.5"
            fill="none"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </button>
      {open && (
        <div className={styles.commandMenu}>
          {commands.map((c) => (
            <button
              key={c.name}
              type="button"
              className={styles.commandMenuItem}
              onClick={() => onPick(c.name)}
            >
              <span className={styles.commandMenuItemName}>/{c.name}</span>
              {c.description && (
                <span className={styles.commandMenuItemDesc}>{c.description}</span>
              )}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
