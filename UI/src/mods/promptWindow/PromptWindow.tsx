import { useEffect, useMemo, useRef, useState } from "react";
import { Button } from "cs2/ui";
import { useValue } from "cs2/api";
import storytellerIcon from "../../assets/storyteller_icon.svg";
import styles from "./PromptWindow.module.scss";
import {
  messagesBinding,
  isRunningBinding,
  tokenSummaryBinding,
  lastErrorBinding,
  availableCommandsBinding,
  submitPrompt,
  cancelRun,
  ChatMessage,
  SlashCommand,
} from "./bindings";

// Top-level Storyteller panel. Toolbar icon (floating variant matches CS2's
// other top-left tool mods) toggles a movable panel containing:
//   - chat history (user / assistant rows, scrollable)
//   - free-form prompt textarea
//   - command-picker dropdown
//   - Run / Cancel button, token-usage line
//
// State that doesn't need to outlive the panel-open session lives locally
// (open/close, draft prompt text, panel position, dropdown state). Anything
// the C# side owns (messages, isRunning, tokens, errors, command list) is
// read via useValue() hooks.
//
// Drag: mousedown on the header records the initial pointer + panel-relative
// position; document-level mousemove updates a {x,y} state that overrides
// the SCSS default positioning. mouseup ends the drag. Listeners attach
// only while dragging — no global handlers when idle.
export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState("");
  const [panelPos, setPanelPos] = useState<{ x: number; y: number } | null>(null);
  const [commandMenuOpen, setCommandMenuOpen] = useState(false);

  const messagesJson = useValue(messagesBinding);
  const isRunning = useValue(isRunningBinding);
  const tokenSummary = useValue(tokenSummaryBinding);
  const lastError = useValue(lastErrorBinding);
  const commandsJson = useValue(availableCommandsBinding);

  const messages = useMemo<ChatMessage[]>(() => {
    try { return JSON.parse(messagesJson); } catch { return []; }
  }, [messagesJson]);

  const commands = useMemo<SlashCommand[]>(() => {
    try { return JSON.parse(commandsJson); } catch { return []; }
  }, [commandsJson]);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages.length]);

  // ---- Drag plumbing ----
  // Tracks the in-flight drag's pointer-anchor and the panel's position at
  // drag-start. Stored in a ref (not state) so the global mousemove handler
  // doesn't have to re-attach when values change.
  const dragRef = useRef<{ startX: number; startY: number; baseX: number; baseY: number } | null>(null);
  const [dragging, setDragging] = useState(false);

  useEffect(() => {
    if (!dragging) return;
    const onMove = (e: MouseEvent) => {
      const d = dragRef.current;
      if (!d) return;
      setPanelPos({ x: d.baseX + (e.clientX - d.startX), y: d.baseY + (e.clientY - d.startY) });
    };
    const onUp = () => setDragging(false);
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
    return () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    };
  }, [dragging]);

  const headerRef = useRef<HTMLDivElement | null>(null);
  const beginDrag = (e: React.MouseEvent) => {
    // Skip when the click is on the close button — let the button's own
    // handler fire without starting a drag.
    if ((e.target as HTMLElement).closest(`.${styles.close}`)) return;
    const panel = headerRef.current?.parentElement;
    const rect = panel?.getBoundingClientRect();
    if (!rect) return;
    dragRef.current = { startX: e.clientX, startY: e.clientY, baseX: rect.left, baseY: rect.top };
    setDragging(true);
  };

  // ---- Submit / commands ----
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

  // When pos is set, use it as inline style; otherwise the SCSS default
  // (top-left near the toolbar icon) applies.
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
        <div className={styles.panel} style={panelStyle}>
          <div
            className={styles.header}
            ref={headerRef}
            onMouseDown={beginDrag}
          >
            <span className={styles.title}>Storyteller</span>
            <button
              type="button"
              className={styles.close}
              onClick={() => setOpen(false)}
            >
              ×
            </button>
          </div>

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
            <CommandMenu
              commands={commands}
              open={commandMenuOpen}
              disabled={isRunning}
              onToggle={() => setCommandMenuOpen((v) => !v)}
              onPick={runCommand}
            />
            <span className={styles.status}>
              {tokenSummary || (isRunning ? "Running…" : "Idle")}
            </span>
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
      )}
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
        Commands ▾
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
