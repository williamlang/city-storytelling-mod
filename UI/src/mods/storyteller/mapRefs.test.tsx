import { render, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { extractMapRefs, MapRefChips } from "./mapRefs";

describe("extractMapRefs", () => {
  it("pulls a coordinate pair out of prose", () => {
    expect(extractMapRefs("centered (820, 1140) in the NE")).toEqual([
      { x: 820, y: 1140 },
    ]);
  });

  it("handles negative coordinates", () => {
    expect(extractMapRefs("the SW flats (-30, -1200) off Loon Lane")).toEqual([
      { x: -30, y: -1200 },
    ]);
  });

  it("collects multiple pairs in first-seen order", () => {
    expect(extractMapRefs("from (10, 20) to (30, 40)")).toEqual([
      { x: 10, y: 20 },
      { x: 30, y: 40 },
    ]);
  });

  it("de-duplicates repeated pairs", () => {
    expect(extractMapRefs("plaza at (-4000, -3500); again at (-4000, -3500)")).toEqual([
      { x: -4000, y: -3500 },
    ]);
  });

  it("returns an empty list when there are no pairs", () => {
    expect(extractMapRefs("Cheng's master plan is on the north bank.")).toEqual([]);
    expect(extractMapRefs("")).toEqual([]);
  });

  it("is stable across calls (regex lastIndex reset)", () => {
    const input = "x (1, 2) y";
    expect(extractMapRefs(input)).toEqual(extractMapRefs(input));
  });
});

describe("MapRefChips", () => {
  it("renders a chip per unique coordinate and fires onGoto on click", () => {
    const onGoto = vi.fn();
    const { container } = render(
      <MapRefChips
        text="site office (820, 1140); subdivision (-430, -1180)"
        onGoto={onGoto}
      />
    );
    const chips = container.querySelectorAll("button");
    expect(chips.length).toBe(2);
    // Click target is a <button>, fine here — chips are out-of-prose block
    // elements, not inline-in-text (which Cohtml can't render).
    fireEvent.click(chips[0]);
    expect(onGoto).toHaveBeenCalledWith(820, 1140);
    fireEvent.click(chips[1]);
    expect(onGoto).toHaveBeenCalledWith(-430, -1180);
    // Coordinate numbers are visible in the chip label.
    expect(container.textContent).toContain("820, 1140");
    expect(container.textContent).toContain("-430, -1180");
  });

  it("renders nothing when the text has no coordinates", () => {
    const { container } = render(
      <MapRefChips text="no coordinates here" onGoto={() => {}} />
    );
    expect(container.querySelector("button")).toBeNull();
    expect(container.firstChild).toBeNull();
  });
});
