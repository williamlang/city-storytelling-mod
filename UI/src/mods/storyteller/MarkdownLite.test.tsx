import { render } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { MarkdownLite } from "./MarkdownLite";

describe("MarkdownLite link rendering", () => {
  it("renders link as an inline <a> with its text", () => {
    const { container } = render(
      <MarkdownLite>{"She knew [Magnus Lindgren](magnus-lindgren.md) the way"}</MarkdownLite>
    );
    expect(container.textContent).toContain("She knew Magnus Lindgren the way");
    const a = container.querySelector("a");
    expect(a?.textContent).toBe("Magnus Lindgren");
  });

  it("calls onLinkClick with the raw href when an internal link is clicked", () => {
    let clicked: string | null = null;
    const { container } = render(
      <MarkdownLite onLinkClick={(href) => (clicked = href)}>
        {"She knew [Magnus](magnus-lindgren.md) the way"}
      </MarkdownLite>
    );
    (container.querySelector("a") as HTMLElement).click();
    expect(clicked).toBe("magnus-lindgren.md");
  });

  it("does not wire a click on external links", () => {
    let clicked = false;
    const { container } = render(
      <MarkdownLite onLinkClick={() => (clicked = true)}>
        {"see [the docs](https://example.com/x) for more"}
      </MarkdownLite>
    );
    (container.querySelector("a") as HTMLElement).click();
    expect(clicked).toBe(false);
  });

  it("preserves the space between text and a link", () => {
    const { container } = render(
      <MarkdownLite>{"interviewed [Erik](erik-lindgren.md) on July 30"}</MarkdownLite>
    );
    expect(container.textContent).toContain("interviewed Erik on July 30");
  });

  it("renders the cole-tatum opening paragraph in source order", () => {
    const para =
      "Cole was born in 1972 in Coeur d'Alene — the same year his " +
      "grandparents Wendell and Margaret Tatum opened the original " +
      "[Hayloft Steakhouse](../places/hayloft-steakhouse.md) on the south " +
      "shore of Hayden Lake.";
    const { container } = render(<MarkdownLite>{para}</MarkdownLite>);
    const rendered = container.textContent ?? "";
    expect(rendered).toContain("opened the original Hayloft Steakhouse on the south");
  });

  it("renders bold, italic, and code as their respective inline elements", () => {
    const { container } = render(
      <MarkdownLite>{"He is **decisive**, *quick*, and answers `yes` first."}</MarkdownLite>
    );
    expect(container.querySelector("strong")?.textContent).toBe("decisive");
    expect(container.querySelector("em")?.textContent).toBe("quick");
    expect(container.querySelector("code")?.textContent).toBe("yes");
  });

  it("leaves coordinate pairs as plain text when onMapGoto is omitted", () => {
    const { container } = render(
      <MarkdownLite>
        {"Halverson's founding plaza at (-4000, -3500), on a bench above the SW river bottom."}
      </MarkdownLite>
    );
    expect(container.querySelector("a")).toBeNull();
    expect(container.textContent).toContain("founding plaza at (-4000, -3500), on a bench");
  });

  it("renders coordinate pairs as inline camera-jump links when onMapGoto is supplied", () => {
    let jumped: [number, number] | null = null;
    const { container } = render(
      <MarkdownLite onMapGoto={(x, y) => (jumped = [x, y])}>
        {"Halverson's founding plaza at (-4000, -3500), on a bench above the river."}
      </MarkdownLite>
    );
    const a = container.querySelector("a");
    expect(a?.textContent).toBe("(-4000, -3500)");
    // Literal coordinate text stays in the prose.
    expect(container.textContent).toContain("founding plaza at (-4000, -3500), on a bench");
    (a as HTMLElement).click();
    expect(jumped).toEqual([-4000, -3500]);
  });

  it("linkifies coordinates that sit next to markdown tokens", () => {
    const onMapGoto = vi.fn();
    const { container } = render(
      <MarkdownLite onMapGoto={onMapGoto}>
        {"the **mill** at (820, 1140) and the [yard](yard.md)"}
      </MarkdownLite>
    );
    expect(container.querySelector("strong")?.textContent).toBe("mill");
    const links = container.querySelectorAll("a");
    // One coordinate link + one markdown link.
    expect(links.length).toBe(2);
  });
});
