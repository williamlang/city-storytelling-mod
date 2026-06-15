import { useEffect, useRef, useState, MouseEvent as ReactMouseEvent } from "react";

// Reusable drag hook for floating panels / modals. Returns:
//   pos        — current { x, y } in viewport pixels, or null until first drag
//   beginDrag  — onMouseDown handler for the drag handle (panel header etc.)
//   dragging   — true while a drag is in flight (caller can use for visuals)
//
// Drag plumbing: on mousedown we record the pointer anchor + the dragged
// element's current viewport position; document-level mousemove updates the
// {x,y} state until mouseup. Listeners attach only while dragging — no
// global handlers when idle. Cancel-on-close-button check is the caller's
// responsibility (pass a `skipIfTarget` selector or check inside the
// onMouseDown before calling beginDrag).
export function useDrag() {
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const [dragging, setDragging] = useState(false);
  const dragRef = useRef<{
    startX: number;
    startY: number;
    baseX: number;
    baseY: number;
  } | null>(null);

  useEffect(() => {
    if (!dragging) return;
    const onMove = (e: MouseEvent) => {
      const d = dragRef.current;
      if (!d) return;
      setPos({
        x: d.baseX + (e.clientX - d.startX),
        y: d.baseY + (e.clientY - d.startY),
      });
    };
    const onUp = () => setDragging(false);
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
    return () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    };
  }, [dragging]);

  // Pass the element being dragged so we can read its current viewport
  // position (which respects any prior drag-to-position) and pin the new
  // drag relative to that. Without this the second drag would snap back
  // to the original CSS-default position.
  //
  // This getBoundingClientRect read is safe under Cohtml's once-per-frame
  // layout (it returns the *previous* frame's geometry): it fires from a
  // mousedown handler against an element that's been sitting settled, with
  // no layout-affecting JS write earlier in this frame — so "previous
  // frame" == current truth. No requestAnimationFrame deferral needed here.
  // (Cohtml layout timing: UI/coherent.md → "once-per-frame layout".)
  const beginDrag = (e: ReactMouseEvent, draggedEl: HTMLElement | null) => {
    if (!draggedEl) return;
    const rect = draggedEl.getBoundingClientRect();
    dragRef.current = {
      startX: e.clientX,
      startY: e.clientY,
      baseX: rect.left,
      baseY: rect.top,
    };
    setDragging(true);
  };

  return { pos, beginDrag, dragging };
}
