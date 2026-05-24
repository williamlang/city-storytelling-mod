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
  cartoExportingBinding,
  cartoAvailableBinding,
  submitPrompt,
  cancelRun,
  refreshGeography,
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

  // Local "pending" state for the Refresh map button. The C# cartoExporting
  // binding round-trips through Coherent UI, but the main thread blocks
  // during Carto's synchronous export — so the binding flips true → false
  // entirely while the UI can't composite a new frame, and the user sees
  // zero visual change. Tracking pending locally guarantees instant feedback
  // on click. Auto-clears after 6 s as a safety net.
  const [refreshPending, setRefreshPending] = useState(false);
  // Minimum hold timestamp: how long the pending state must stay visible
  // regardless of when the C# side flips the binding back to false. Without
  // this, a sub-second Carto export clears the indicator before the player
  // can register the visual change.
  const [refreshPendingUntil, setRefreshPendingUntil] = useState(0);
  // Sticky post-completion confirmation. When set, the footer shows
  // "Map updated" for a few seconds so the player knows their click did
  // something even if they missed the "Updating…" transition.
  const [refreshConfirm, setRefreshConfirm] = useState<string | null>(null);

  const messagesJson = useValue(messagesBinding);
  const isRunning = useValue(isRunningBinding);
  const tokenSummary = useValue(tokenSummaryBinding);
  const lastError = useValue(lastErrorBinding);
  const commandsJson = useValue(availableCommandsBinding);
  const canonJson = useValue(canonTreeBinding);
  const cartoExporting = useValue(cartoExportingBinding);
  const cartoAvailable = useValue(cartoAvailableBinding);

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

  // Safety net: if neither the C# binding flip nor the minimum-hold timer
  // clears refreshPending, drop it after 6 s. Catches the rare error path
  // where Carto throws before the binding can flip back to false.
  useEffect(() => {
    if (!refreshPending) return;
    const t = setTimeout(() => {
      setRefreshPending(false);
      setRefreshPendingUntil(0);
    }, 6000);
    return () => clearTimeout(t);
  }, [refreshPending]);

  // Clear pending when the C# binding goes false AND the minimum hold time
  // has elapsed. The minimum hold makes sure a sub-second Carto export
  // doesn't blink the "Updating…" indicator faster than the player can see.
  useEffect(() => {
    if (cartoExporting || !refreshPending) return;
    const now = Date.now();
    const remaining = refreshPendingUntil - now;
    if (remaining <= 0) {
      setRefreshPending(false);
      setRefreshConfirm("Map data updated");
    } else {
      const t = setTimeout(() => {
        setRefreshPending(false);
        setRefreshConfirm("Map data updated");
      }, remaining);
      return () => clearTimeout(t);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cartoExporting, refreshPending, refreshPendingUntil]);

  // Confirmation message clears itself after a few seconds so it doesn't
  // permanently pin to the footer.
  useEffect(() => {
    if (!refreshConfirm) return;
    const t = setTimeout(() => setRefreshConfirm(null), 4000);
    return () => clearTimeout(t);
  }, [refreshConfirm]);

  const refreshDisabled = refreshPending || cartoExporting;
  const handleRefreshClick = () => {
    if (refreshDisabled) return;
    setRefreshPending(true);
    setRefreshPendingUntil(Date.now() + 1500);  // minimum visible hold
    setRefreshConfirm(null);
    refreshGeography();
  };

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
  const closeAllFiles = () => setOpenModals([]);

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
        aria-label="Ghostwriter"
      >
        <img src={storytellerIcon} className={styles.toolbarIcon} alt="" />
      </Button>

      {open && (
        <div className={styles.panel} style={panelStyle} ref={panelRef}>
          <div className={styles.header} onMouseDown={onHeaderMouseDown}>
            <span className={styles.title}>Ghostwriter</span>
            <button
              type="button"
              className={styles.close}
              onClick={() => setOpen(false)}
            >
              ×
            </button>
          </div>

          {(refreshPending || cartoExporting) ? (
            <div className={styles.cartoBanner}>
              <span className={styles.cartoBannerDot} />
              Updating spatial data…
            </div>
          ) : refreshConfirm ? (
            <div className={`${styles.cartoBanner} ${styles.cartoBannerOk}`}>
              {refreshConfirm}
            </div>
          ) : null}

          <div className={styles.body}>
            <div className={styles.main}>
              <div className={styles.chat} ref={scrollRef}>
                {messages.length === 0 && (
                  <div className={styles.empty}>
                    Ask the ghostwriter to do something. Free-form prompts or pick
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
                  {cartoAvailable && (
                    <button
                      type="button"
                      className={styles.secondary}
                      disabled={refreshDisabled}
                      title="Update spatial data via Carto. Locks the main thread briefly."
                      onClick={handleRefreshClick}
                    >
                      {refreshDisabled ? "Updating…" : "Refresh map"}
                    </button>
                  )}
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
              onCloseAll={closeAllFiles}
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
          onOpenFile={openFile}
        />
      ))}
    </>
  );
}
