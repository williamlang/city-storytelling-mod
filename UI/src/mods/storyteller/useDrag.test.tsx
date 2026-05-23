import { describe, it, expect } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useDrag } from "./useDrag";

// useDrag pins the dragged element's screen position via mousemove deltas
// from the initial pointer location. These tests stub getBoundingClientRect
// on a fake element and dispatch native MouseEvents to drive the hook.

function makeElement(top: number, left: number, width = 200, height = 100): HTMLElement {
  const el = document.createElement("div");
  el.getBoundingClientRect = () => ({
    top, left,
    bottom: top + height,
    right: left + width,
    width, height,
    x: left, y: top,
    toJSON() {},
  } as DOMRect);
  return el;
}

describe("useDrag", () => {
  it("starts with pos=null and dragging=false", () => {
    const { result } = renderHook(() => useDrag());
    expect(result.current.pos).toBeNull();
    expect(result.current.dragging).toBe(false);
  });

  it("sets dragging=true when beginDrag is called with an element", () => {
    const { result } = renderHook(() => useDrag());
    const el = makeElement(50, 100);
    act(() => {
      result.current.beginDrag({ clientX: 110, clientY: 60 } as any, el);
    });
    expect(result.current.dragging).toBe(true);
  });

  it("updates pos by the mousemove delta from drag-start", () => {
    const { result } = renderHook(() => useDrag());
    const el = makeElement(50, 100);
    act(() => {
      result.current.beginDrag({ clientX: 110, clientY: 60 } as any, el);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mousemove", { clientX: 130, clientY: 80 }));
    });
    // Pointer moved +20/+20; element base was at (100, 50). New pos = (120, 70).
    expect(result.current.pos).toEqual({ x: 120, y: 70 });
  });

  it("stops dragging on mouseup but keeps pos", () => {
    const { result } = renderHook(() => useDrag());
    const el = makeElement(50, 100);
    act(() => {
      result.current.beginDrag({ clientX: 110, clientY: 60 } as any, el);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mousemove", { clientX: 130, clientY: 80 }));
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mouseup"));
    });
    expect(result.current.dragging).toBe(false);
    expect(result.current.pos).toEqual({ x: 120, y: 70 });
  });

  it("does nothing when beginDrag is called with a null element", () => {
    const { result } = renderHook(() => useDrag());
    act(() => {
      result.current.beginDrag({ clientX: 0, clientY: 0 } as any, null);
    });
    expect(result.current.dragging).toBe(false);
    expect(result.current.pos).toBeNull();
  });

  it("subsequent drags pin to the latest element position, not the original", () => {
    const { result } = renderHook(() => useDrag());

    // First drag: element at (100, 50), drag to (120, 70).
    const el1 = makeElement(50, 100);
    act(() => {
      result.current.beginDrag({ clientX: 110, clientY: 60 } as any, el1);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mousemove", { clientX: 130, clientY: 80 }));
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mouseup"));
    });
    expect(result.current.pos).toEqual({ x: 120, y: 70 });

    // Second drag: element now reports its NEW position (caller would have
    // re-rendered with the updated style). Drag should pin to that, not
    // snap back to (100, 50).
    const el2 = makeElement(70, 120);
    act(() => {
      result.current.beginDrag({ clientX: 200, clientY: 200 } as any, el2);
    });
    act(() => {
      document.dispatchEvent(new MouseEvent("mousemove", { clientX: 250, clientY: 240 }));
    });
    // Pointer moved +50/+40 from drag-start; element base (120, 70). New pos = (170, 110).
    expect(result.current.pos).toEqual({ x: 170, y: 110 });
  });
});
