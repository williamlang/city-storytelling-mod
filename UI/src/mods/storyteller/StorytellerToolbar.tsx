import { useEffect, useMemo, useRef, useState } from "react";
import { Button } from "cs2/ui";
import { useValue } from "cs2/api";
import storytellerIcon from "../../assets/storyteller_icon.svg";
import styles from "./storyteller.module.scss";
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
} from "./bindings";
import type { ChatMessage, SlashCommand, CanonTree, CanonEntry } from "./bindings";
import { ChatRow } from "./ChatRow";
import { CommandMenu } from "./CommandMenu";
import { CanonBrowser } from "./CanonBrowser";
import { FileModal } from "./FileModal";

// Top-level Storyteller entry. The toolbar icon (floating variant matches
// CS2's other top-left tool mods) toggles a draggable panel with:
//   - chat history (user / assistant rows, scrollable)
//   - free-form prompt textarea
//   - command-picker dropdown + Run/Cancel
//   - side panel with a canon browser
//
// This component is the state-coordinating shell — it reads bindings,
// drives drag state for the panel, and tracks which canon-file modals
// are open. Each visual sub-region lives in its own component file
// (ChatRow, CommandMenu, CanonBrowser, FileModal).
//
// Open modals are tracked at this level (not inside CanonBrowser) so
// closing the side panel doesn't dismiss them. Whether that's the right
// UX is debatable — for now they stick around.
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

  // Flatten the tree once so FileModal can look up content by path
  // without re-walking the tree on each render.
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
