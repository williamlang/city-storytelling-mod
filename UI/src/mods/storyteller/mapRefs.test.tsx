import { render, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { extractMapRefs, renderTextWithMapRefs } from "./mapRefs";

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

  it("handles thousands separators inside the numbers", () => {
    // The storyteller sometimes writes "-1,500" instead of "-1500"; the inner
    // comma must not be mistaken for the x/y delimiter.
    expect(extractMapRefs("the old yards at (-1,500, -800)")).toEqual([
      { x: -1500, y: -800 },
    ]);
    expect(extractMapRefs("Pearl & 4th, approximately (-2,050, -1,470)")).toEqual([
      { x: -2050, y: -1470 },
    ]);
  });

  it("matches a pair nested inside other parens / prefixes", () => {
    expect(extractMapRefs("the mill (~(-2,180, -1,780))")).toEqual([
      { x: -2180, y: -1780 },
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

describe("renderTextWithMapRefs", () => {
  it("renders each coordinate pair as an inline link and fires onGoto on click", () => {
    const onGoto = vi.fn();
    const { container } = render(
      <div>{renderTextWithMapRefs("site office (820, 1140); subdivision (-430, -1180)", onGoto, "t")}</div>
    );
    const links = container.querySelectorAll("a");
    expect(links.length).toBe(2);
    // The literal coordinate text is preserved inline so prose reads naturally.
    expect(container.textContent).toBe("site office (820, 1140); subdivision (-430, -1180)");
    fireEvent.click(links[0]);
    expect(onGoto).toHaveBeenCalledWith(820, 1140);
    fireEvent.click(links[1]);
    expect(onGoto).toHaveBeenCalledWith(-430, -1180);
  });

  it("strips thousands separators when computing the jump target", () => {
    const onGoto = vi.fn();
    const { container } = render(
      <div>{renderTextWithMapRefs("the yards at (-1,500, -800)", onGoto, "t")}</div>
    );
    fireEvent.click(container.querySelector("a") as HTMLElement);
    expect(onGoto).toHaveBeenCalledWith(-1500, -800);
  });

  it("emits no links when the text has no coordinates", () => {
    const { container } = render(
      <div>{renderTextWithMapRefs("no coordinates here", () => {}, "t")}</div>
    );
    expect(container.querySelector("a")).toBeNull();
    expect(container.textContent).toBe("no coordinates here");
  });

  it("leaves text untouched when onGoto is omitted", () => {
    const { container } = render(
      <div>{renderTextWithMapRefs("plaza at (820, 1140)", undefined, "t")}</div>
    );
    expect(container.querySelector("a")).toBeNull();
    expect(container.textContent).toBe("plaza at (820, 1140)");
  });
});
