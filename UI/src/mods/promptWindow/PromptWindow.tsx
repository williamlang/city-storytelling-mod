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
  submitPrompt,
  cancelRun,
  ChatMessage,
} from "./bindings";

// Top-level Storyteller panel. Toolbar icon (floating variant matches CS2's
// other top-left tool mods) toggles a dropdown panel containing:
//   - scrollable chat history (user / assistant / tool rows)
//   - free-form prompt textarea
//   - Run / Cancel button, token-usage line, clear-chat link
//
// All state lives in C# (Systems/PromptUISystem.cs); the React side reads via
// useValue() hooks and fires triggers for user actions. No local list — the
// messagesBinding JSON-string is parsed once per change.
export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState("");

  const messagesJson = useValue(messagesBinding);
  const isRunning = useValue(isRunningBinding);
  const tokenSummary = useValue(tokenSummaryBinding);
  const lastError = useValue(lastErrorBinding);

  const messages = useMemo<ChatMessage[]>(() => {
    try { return JSON.parse(messagesJson); } catch { return []; }
  }, [messagesJson]);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages.length]);

  const canSubmit = draft.trim().length > 0 && !isRunning;
  const handleSubmit = () => {
    if (!canSubmit) return;
    submitPrompt(draft.trim());
    setDraft("");
  };

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
        <div className={styles.panel}>
          <div className={styles.header}>
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
                Ask the storyteller to do something. Free-form prompts or
                <code className={styles.code}> /command</code> names.
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
            placeholder={isRunning ? "Running…" : "Type a prompt or /command…"}
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
  const roleClass =
    msg.role === "user" ? styles.userRow
    : msg.role === "assistant" ? styles.assistantRow
    : styles.toolRow;
  const isToolError = msg.role === "tool" && msg.isError;
  return (
    <div className={`${styles.row} ${roleClass} ${isToolError ? styles.errorRow : ""}`}>
      <span className={styles.role}>{msg.role}</span>
      <div className={styles.body}>
        {msg.text && <div className={styles.text}>{msg.text}</div>}
        {msg.toolCalls && <div className={styles.toolCalls}>↳ {msg.toolCalls}</div>}
      </div>
    </div>
  );
}
