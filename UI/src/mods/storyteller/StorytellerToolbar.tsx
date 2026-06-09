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
  setupNeededBinding,
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

// Custom scrollbar for the chat. Coherent UI doesn't render any visible
// scrollbar — neither ::-webkit-scrollbar pseudos nor the standard
// scrollbar-color/scrollbar-width have any effect — so we draw our own
// track/thumb absolutely positioned over .chatWrap's right edge and wire
// it for drag + track-click. Works in the dev harness against real Chrome
// too, where it overlays the native scrollbar harmlessly.
//
// Subscribes to scroll + ResizeObserver so the thumb tracks both scrolling
// and content-size changes (a streaming run that adds rows shrinks the
// scrollHeight ratio; this keeps the thumb size honest). The thumb is
// draggable and the track is click-to-jump — both translate a pixel
// position on the track back into el.scrollTop. We use mouse events (not
// Pointer Events / setPointerCapture, which Cohtml 1.64 doesn't reliably
// support) and attach the move/up listeners to window for the drag's
// lifetime so the grab survives the cursor leaving the thin track.
function ChatScrollIndicator(props: { scrollRef: React.RefObject<HTMLDivElement> }) {
  const [state, setState] = useState<{ topPct: number; heightPct: number; visible: boolean }>({
    topPct: 0,
    heightPct: 100,
    visible: false,
  });
  const trackRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const el = props.scrollRef.current;
    if (!el) return;
    const update = () => {
      const sh = el.scrollHeight;
      const ch = el.clientHeight;
      if (sh <= ch) {
        setState({ topPct: 0, heightPct: 100, visible: false });
        return;
      }
      const ratio = ch / sh;
      const heightPct = ratio * 100;
      const maxScroll = sh - ch;
      const scrolled = maxScroll > 0 ? el.scrollTop / maxScroll : 0;
      const topPct = scrolled * (100 - heightPct);
      setState({ topPct, heightPct, visible: true });
    };
    update();
    el.addEventListener("scroll", update);
    const ro = new ResizeObserver(update);
    ro.observe(el);
    // Also re-run when child count changes — ResizeObserver only fires
    // on size changes, but if rows are added and the chat is already
    // at max height, scrollHeight grows without a resize event.
    const mo = new MutationObserver(update);
    mo.observe(el, { childList: true, subtree: true });
    return () => {
      el.removeEventListener("scroll", update);
      ro.disconnect();
      mo.disconnect();
    };
  }, [props.scrollRef]);

  // Drag the thumb. Pixels moved on the track map to scrollTop via the
  // ratio of scrollable content to the thumb's travel distance.
  const onThumbDown = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation(); // don't let the track's click-to-jump also fire
    const el = props.scrollRef.current;
    const track = trackRef.current;
    if (!el || !track) return;
    const startY = e.clientY;
    const startScrollTop = el.scrollTop;
    const trackH = track.clientHeight;
    const sh = el.scrollHeight;
    const ch = el.clientHeight;
    const thumbH = trackH * (ch / sh);
    const travel = trackH - thumbH;
    const scrollPerPx = travel > 0 ? (sh - ch) / travel : 0;
    const onMove = (ev: MouseEvent) => {
      el.scrollTop = startScrollTop + (ev.clientY - startY) * scrollPerPx;
    };
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    };
    // document-level listeners (matching useDrag) — proven to fire in Cohtml,
    // and they keep the grab alive while the cursor leaves the thin track.
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  };

  // Click anywhere on the track (outside the thumb) to jump so the click
  // point becomes the thumb's new center.
  const onTrackDown = (e: React.MouseEvent) => {
    const el = props.scrollRef.current;
    const track = trackRef.current;
    if (!el || !track) return;
    const rect = track.getBoundingClientRect();
    const frac = rect.height > 0 ? (e.clientY - rect.top) / rect.height : 0;
    el.scrollTop = frac * (el.scrollHeight - el.clientHeight) - el.clientHeight / 2;
  };

  if (!state.visible) return null;
  return (
    <div
      ref={trackRef}
      className={styles.scrollIndicatorTrack}
      // Background set inline, not via the SCSS class: Cohtml drops the
      // `background` declaration on these two elements specifically (verified via
      // CDP — it computes transparent, while the same shorthand renders fine
      // elsewhere), leaving the scrollbar invisible. An inline style is honored
      // reliably.
      style={{ backgroundColor: "rgba(255, 255, 255, 0.08)" }}
      onMouseDown={onTrackDown}
    >
      <div
        className={styles.scrollIndicatorThumb}
        style={{
          top: `${state.topPct}%`,
          height: `${state.heightPct}%`,
          backgroundColor: "rgba(255, 255, 255, 0.5)",
        }}
        onMouseDown={onThumbDown}
      />
    </div>
  );
}

export function StorytellerToolbar() {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState("");
  const [commandMenuOpen, setCommandMenuOpen] = useState(false);
  const [openModals, setOpenModals] = useState<string[]>([]);
  // Flash the toolbar icon when a response arrives while the panel is closed,
  // so the player notices the ghostwriter replied (or an autonomous event
  // fired) without the window open. Cleared the moment they open it.
  const [hasUnseen, setHasUnseen] = useState(false);

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
  const setupNeeded = useValue(setupNeededBinding);
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
  // Pin the chat to the bottom on new messages AND whenever the panel opens
  // (the player expects to land on the latest turn, not wherever they last
  // left the scroll). A single requestAnimationFrame isn't enough: Cohtml lays
  // the freshly-mounted rows out over several frames, so the first frame reads
  // a stale (short) scrollHeight and the jump lands mid-history. Instead we
  // re-pin every frame until scrollHeight stops growing (layout settled) or we
  // hit a frame cap — robust to Cohtml's deferred layout.
  useEffect(() => {
    if (!open) return;
    const el = scrollRef.current;
    if (!el) return;
    let raf = 0;
    let frames = 0;
    const pin = () => {
      // Two Cohtml quirks, both verified via CDP:
      //  1. scrollTop is NOT clamped to the max, so `= scrollHeight` overshoots
      //     a full viewport (lands clientHeight px past the last row). The real
      //     bottom is scrollHeight - clientHeight.
      //  2. Rows lay out over several frames AND scrollHeight sits at its
      //     pre-layout value for a frame or two first — so a "stop when
      //     scrollHeight stops changing" loop concludes "settled" at the top
      //     before the rows ever get their height. Instead just re-pin every
      //     frame for a fixed window so we catch the layout once it lands.
      el.scrollTop = el.scrollHeight - el.clientHeight;
      if (++frames < 40) raf = requestAnimationFrame(pin);
    };
    raf = requestAnimationFrame(pin);
    return () => cancelAnimationFrame(raf);
  }, [open, messages.length]);

  // Mark an unseen response when the message count grows with an assistant turn
  // while the panel is closed. Tracks the prior count so a clear (count drops)
  // or our own open doesn't trip it.
  const prevMsgLenRef = useRef(messages.length);
  useEffect(() => {
    const prev = prevMsgLenRef.current;
    prevMsgLenRef.current = messages.length;
    if (!open && messages.length > prev) {
      const last = messages[messages.length - 1];
      if (last && last.role === "assistant") setHasUnseen(true);
    }
  }, [messages, open]);

  // Opening the panel clears the unseen flag (they've now seen it).
  useEffect(() => {
    if (open) setHasUnseen(false);
  }, [open]);

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
        <img
          src={storytellerIcon}
          className={`${styles.toolbarIcon} ${hasUnseen ? styles.toolbarIconFlash : ""}`}
          alt=""
        />
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

          {setupNeeded && (
            <div className={styles.setupBanner}>
              {/* Block-level lines only — Cohtml can't inline-flow elements
                  within running text, so no inline <strong>/<em>. */}
              <div className={styles.setupBannerTitle}>No language model set up yet</div>
              <div>
                Open Options → Ghostwriter, choose a provider, and paste an API
                key (or set up the Claude Code CLI). Prompts won&rsquo;t run until
                a provider is configured.
              </div>
            </div>
          )}

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
              <div className={styles.chatWrap}>
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
              <ChatScrollIndicator scrollRef={scrollRef} />
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
