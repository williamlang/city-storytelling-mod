import { useEffect, useRef, useState, MouseEvent as ReactMouseEvent } from "react";

// Resize hook for floating panels — companion to useDrag. Returns:
//   size       — current { w, h } in CSS pixels, or null until first resize
//   beginResize — onMouseDown handler for the resize grip (SE corner etc.)
//   resizing   — true while a resize is in flight
//
// Plumbing mirrors useDrag: on mousedown we record the pointer anchor +
// the panel's current size; document-level mousemove updates the size state
// until mouseup. The first beginResize call reads the panel's current
// bounding rect so subsequent resizes start from wherever the panel
// actually is, not from a CSS default.
//
// Bounds (min/max width/height) clamp the size on every move so the panel
// can't shrink past usable or grow past the viewport.
export function useResize(opts: {
  minW: number;
  maxW: number;
  minH: number;
  maxH: number;
}) {
  const { minW, maxW, minH, maxH } = opts;
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const [resizing, setResizing] = useState(false);
  const dragRef = useRef<{
    startX: number;
    startY: number;
    baseW: number;
    baseH: number;
  } | null>(null);

  useEffect(() => {
    if (!resizing) return;
    const onMove = (e: MouseEvent) => {
      const d = dragRef.current;
      if (!d) return;
      const w = Math.max(minW, Math.min(maxW, d.baseW + (e.clientX - d.startX)));
      const h = Math.max(minH, Math.min(maxH, d.baseH + (e.clientY - d.startY)));
      setSize({ w, h });
    };
    const onUp = () => setResizing(false);
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
    return () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    };
  }, [resizing, minW, maxW, minH, maxH]);

  const beginResize = (e: ReactMouseEvent, resizedEl: HTMLElement | null) => {
    if (!resizedEl) return;
    e.stopPropagation();  // don't let the header's drag handler also fire
    e.preventDefault();
    // Safe under Cohtml's once-per-frame layout for the same reason as
    // useDrag.beginDrag: a mousedown read of a settled element, no
    // layout-affecting write earlier this frame, so the previous-frame
    // geometry this returns is the current truth. (UI/coherent.md.)
    const rect = resizedEl.getBoundingClientRect();
    dragRef.current = {
      startX: e.clientX,
      startY: e.clientY,
      baseW: rect.width,
      baseH: rect.height,
    };
    setResizing(true);
  };

  return { size, beginResize, resizing };
}
