import { useEffect, useMemo, useRef, useState } from "react";
import { Button } from "cs2/ui";
import { useValue } from "cs2/api";
import storytellerIcon from "../../assets/storyteller_icon.svg";
import styles from "./storyteller.module.scss";
import { useDrag } from "./useDrag";
import { useResize } from "./useResize";
import {
  messagesBinding,
  isRunningBinding,
  tokenSummaryBinding,
  lastErrorBinding,
  availableCommandsBinding,
  canonTreeBinding,
  cartoExportingBinding,
  cartoAvailableBinding,
  activeEventsEnabledBinding,
  nextEventAtUtcSecBinding,
  activeEventsPausedBinding,
  openEventsBinding,
  submitPrompt,
  cancelRun,
  refreshGeography,
  setActiveEventsEnabled,
} from "./bindings";
import type { ChatMessage, SlashCommand, CanonTree, CanonEntry, OpenEvent } from "./bindings";
import { ChatRow } from "./ChatRow";
import { CommandMenu } from "./CommandMenu";
import { CanonBrowser } from "./CanonBrowser";
import { FileModal } from "./FileModal";
import { OpenEventsInbox } from "./OpenEventsInbox";

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
  const openEventsJson = useValue(openEventsBinding);
  const cartoExporting = useValue(cartoExportingBinding);
  const cartoAvailable = useValue(cartoAvailableBinding);
  const activeEventsEnabled = useValue(activeEventsEnabledBinding);
  const nextEventAtUtcSec = useValue(nextEventAtUtcSecBinding);
  const activeEventsPaused = useValue(activeEventsPausedBinding);

  // 1Hz tick so the countdown re-renders without the C# side thrashing
  // the binding. nextEventAtUtcSec only changes when the deadline
  // anchor shifts (run fires, interval setting changes, toggle flips);
  // the visible MM:SS comes from this local clock subtracting it.
  // Skip the tick entirely while paused — frozenRemainingMs holds the
  // captured value across the pause.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    if (!activeEventsEnabled || activeEventsPaused) return;
    const t = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(t);
  }, [activeEventsEnabled, activeEventsPaused]);

  // Capture the visible remaining at the moment paused flips to true,
  // so the displayed value sits still through the entire pause. Cleared
  // when paused flips back to false (live countdown resumes from the
  // C# side's freshly-advanced deadline).
  const [frozenRemainingMs, setFrozenRemainingMs] = useState<number | null>(null);
  useEffect(() => {
    if (activeEventsPaused) {
      setFrozenRemainingMs(Math.max(0, nextEventAtUtcSec * 1000 - Date.now()));
    } else {
      setFrozenRemainingMs(null);
    }
    // Intentionally NOT depending on nextEventAtUtcSec — we want the
    // snapshot at the moment of the pause edge, not a moving target.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeEventsPaused]);

  // Format remaining seconds as M:SS / MM:SS, capped at 99 minutes for
  // layout sanity (caller's interval slider tops out at 60 anyway).
  // Negative remaining renders as "ready" — the deadline has passed
  // but the loop hasn't fired yet (idle, queue full, or dispatcher busy).
  const activeLabel = (() => {
    if (!activeEventsEnabled) return "Active: off";
    if (!nextEventAtUtcSec) return activeEventsPaused ? "Active: on paused" : "Active: on";
    const remainingMs = activeEventsPaused
      ? (frozenRemainingMs ?? 0)
      : nextEventAtUtcSec * 1000 - nowMs;
    const suffix = activeEventsPaused ? " paused" : "";
    if (remainingMs <= 0) return `Active: on · ready${suffix}`;
    const totalSec = Math.floor(remainingMs / 1000);
    const m = Math.min(99, Math.floor(totalSec / 60));
    const s = totalSec % 60;
    return `Active: on · ${m}:${s.toString().padStart(2, "0")}${suffix}`;
  })();

  const messages = useMemo<ChatMessage[]>(() => {
    try { return JSON.parse(messagesJson); } catch { return []; }
  }, [messagesJson]);

  const commands = useMemo<SlashCommand[]>(() => {
    try { return JSON.parse(commandsJson); } catch { return []; }
  }, [commandsJson]);

  const canonTree = useMemo<CanonTree>(() => {
    try { return JSON.parse(canonJson); } catch { return {}; }
  }, [canonJson]);

  const openEvents = useMemo<OpenEvent[]>(() => {
    try { return JSON.parse(openEventsJson); } catch { return []; }
  }, [openEventsJson]);

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
  // Panel resize. Minimum is enough to leave the canon sidebar (240rem) and
  // a usable chat column (~280rem) breathing room; maximum is the viewport
  // so the player can't lose the panel off-screen. The hook clamps on every
  // move so values stay in range even on fast drags.
  const { size: panelSize, beginResize } = useResize({
    minW: 560, minH: 320,
    maxW: typeof window !== "undefined" ? window.innerWidth - 40 : 1920,
    maxH: typeof window !== "undefined" ? window.innerHeight - 40 : 1080,
  });
  const onHeaderMouseDown = (e: React.MouseEvent) => {
    // Skip when the click is on the close button so its handler fires
    // without starting a drag.
    if ((e.target as HTMLElement).closest(`.${styles.close}`)) return;
    beginDrag(e, panelRef.current);
  };
  const onResizeHandleMouseDown = (e: React.MouseEvent) => {
    beginResize(e, panelRef.current);
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

  // Merge drag position and resize size into a single inline style. Either
  // can be set independently; if both are null we let the CSS defaults apply.
  const panelStyle: React.CSSProperties | undefined =
    (panelPos || panelSize)
      ? {
          ...(panelPos ? { top: `${panelPos.y}px`, left: `${panelPos.x}px` } : {}),
          ...(panelSize
            ? {
                width: `${panelSize.w}px`,
                height: `${panelSize.h}px`,
                maxHeight: `${panelSize.h}px`,
              }
            : {}),
        }
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
            <span className={styles.title}>
              Ghostwriter
              <span className={styles.buildStamp} title="Bundle build time">
                {__BUILD_TIME__}
              </span>
            </span>
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

          <OpenEventsInbox events={openEvents} onOpen={openFile} />

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
                  <button
                    type="button"
                    className={`${styles.secondary} ${activeEventsEnabled ? styles.toggleOn : ""}`}
                    title={
                      activeEventsEnabled
                        ? "Active events on — the storyteller periodically proposes events and auto-resolves them after each snapshot. Countdown shows wall-clock until the next eligible fire; 'ready' means the deadline passed but the loop is waiting (paused, idle, or queue full). Click to disable."
                        : "Active events off — the storyteller only runs when you invoke it manually. Click to enable autonomous events."
                    }
                    onClick={() => setActiveEventsEnabled(!activeEventsEnabled)}
                  >
                    {activeLabel}
                  </button>
                  {cartoAvailable && (
                    <button
                      type="button"
                      className={`${styles.secondary} ${refreshDisabled ? styles.secondaryPulsing : ""}`}
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

          {/* SE-corner resize grip. Sized large enough to grab without
              squinting; styled as a quiet diagonal hatch in the corner. */}
          <div
            className={styles.resizeHandle}
            onMouseDown={onResizeHandleMouseDown}
            title="Drag to resize"
          />
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
