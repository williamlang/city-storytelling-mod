import { render } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { MarkdownLite } from "./MarkdownLite";

describe("MarkdownLite link rendering", () => {
  it("renders link as plain text — no <a>, no <span>, no click target", () => {
    const { container } = render(
      <MarkdownLite>{"She knew [Magnus Lindgren](magnus-lindgren.md) the way"}</MarkdownLite>
    );
    expect(container.textContent).toContain("She knew Magnus Lindgren the way");
    // No interactive elements emitted from the link.
    expect(container.querySelector("a")).toBeNull();
    expect(container.querySelector('[role="link"]')).toBeNull();
    expect(container.querySelector("button")).toBeNull();
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
});
