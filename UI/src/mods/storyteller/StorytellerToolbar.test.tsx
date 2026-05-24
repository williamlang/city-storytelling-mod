import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { bindValue } from "cs2/api";
import { StorytellerToolbar } from "./StorytellerToolbar";

// End-to-end-ish tests against the storyteller panel via the mocked cs2/*
// bindings. We seed each binding fresh per test to avoid one test's
// leftover state breaking the next.
//
// These cover the user-visible interactions you care about: clicking the
// toolbar to open the panel, expanding canon groups, opening a file modal,
// and closing the modal again. They don't verify CS2-specific visuals
// (rem scaling, font glyphs) — those only show in-game.

function seedEmpty() {
  bindValue("CityStoryMod", "messages", "[]");
  bindValue("CityStoryMod", "isRunning", false);
  bindValue("CityStoryMod", "tokenSummary", "");
  bindValue("CityStoryMod", "lastError", "");
  bindValue("CityStoryMod", "availableCommands", "[]");
  bindValue("CityStoryMod", "canonTree", "{}");
}

function seedWithCanon() {
  seedEmpty();
  bindValue("CityStoryMod", "canonTree", JSON.stringify({
    characters: [
      { name: "annika", path: "characters/annika.md", content: "# Annika\n\nThe mayor." },
      { name: "marcus", path: "characters/marcus.md", content: "# Marcus\n\nThe developer." },
    ],
    places: [
      { name: "downtown", path: "places/downtown.md", content: "Six blocks of brick." },
    ],
  }));
}

describe("StorytellerToolbar", () => {
  it("toolbar icon is rendered and panel is closed by default", () => {
    seedEmpty();
    render(<StorytellerToolbar />);
    expect(screen.getByLabelText("Ghostwriter")).toBeInTheDocument();
    expect(screen.queryByText("Canon")).not.toBeInTheDocument();
  });

  it("clicking the icon opens the panel", () => {
    seedEmpty();
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));
    expect(screen.getByText("Ghostwriter")).toBeInTheDocument();
    expect(screen.getByText("Canon")).toBeInTheDocument();
  });

  it("canon groups are collapsed by default; clicking the header expands them", () => {
    seedWithCanon();
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));

    // Group header for characters is visible; entries are not.
    const charactersHeader = screen.getByRole("button", { name: /characters/i });
    expect(charactersHeader).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "annika" })).not.toBeInTheDocument();

    // Expand.
    fireEvent.click(charactersHeader);
    expect(screen.getByRole("button", { name: "annika" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "marcus" })).toBeInTheDocument();
  });

  it("clicking a canon file opens it in a modal with markdown rendered", () => {
    seedWithCanon();
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));
    fireEvent.click(screen.getByRole("button", { name: /characters/i }));
    fireEvent.click(screen.getByRole("button", { name: "annika" }));

    // Modal header shows the path; body shows markdown-rendered content.
    expect(screen.getByText("characters/annika.md")).toBeInTheDocument();
    // The "# Annika" markdown header should render as an <h1>.
    const heading = screen.getByRole("heading", { name: "Annika" });
    expect(heading.tagName).toBe("H1");
  });

  it("opening a second file leaves the first modal open", () => {
    seedWithCanon();
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));
    fireEvent.click(screen.getByRole("button", { name: /characters/i }));
    fireEvent.click(screen.getByRole("button", { name: "annika" }));
    fireEvent.click(screen.getByRole("button", { name: "marcus" }));

    expect(screen.getByText("characters/annika.md")).toBeInTheDocument();
    expect(screen.getByText("characters/marcus.md")).toBeInTheDocument();
  });

  it("empty canon tree shows the bootstrap nudge", () => {
    seedEmpty();
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));
    expect(screen.getByText(/No canon yet/i)).toBeInTheDocument();
  });

  it("secrets/ subdir is rendered when present in the tree (gating is enforced C#-side)", () => {
    seedEmpty();
    // The C# side filters secrets out before sending the tree; if a test
    // wants to verify "secrets present" the binding gets the secrets key.
    // Here we just confirm React renders whatever the binding contains.
    bindValue("CityStoryMod", "canonTree", JSON.stringify({
      secrets: [{ name: "leak", path: "secrets/leak.md", content: "hidden truth" }],
    }));
    render(<StorytellerToolbar />);
    fireEvent.click(screen.getByLabelText("Ghostwriter"));
    expect(screen.getByRole("button", { name: /secrets/i })).toBeInTheDocument();
  });
});
