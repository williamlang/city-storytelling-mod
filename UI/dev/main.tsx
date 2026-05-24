import { createRoot } from "react-dom/client";
import { StorytellerToolbar } from "../src/mods/storyteller/StorytellerToolbar";
import { seedSampleCity } from "./fixtures/sample-city";

// Seed mock bindings BEFORE rendering so the component sees realistic
// initial state. Edit dev/fixtures/sample-city.ts to change what the
// panel shows on first paint. Vite hot-reloads the fixture on save.
seedSampleCity();

// HMR re-seeding: whenever bindings.ts (or its transitive deps) is hot-
// reloaded, Vite re-evaluates it, which causes the cs2/api mock to
// re-run every `bindValue(..., emptyDefault)` and clobber whatever
// seedSampleCity put there. Without this hook, every edit to bindings.ts
// looks like "the harness suddenly has no fixture data." Re-running the
// seeder on each HMR cycle restores the demo state without a full
// reload.
if (import.meta.hot) {
  import.meta.hot.accept(
    ["../src/mods/storyteller/bindings", "./fixtures/sample-city"],
    () => seedSampleCity()
  );
}

// Scale the 1920×1080 game canvas to fit the browser viewport. The panel
// inside uses absolute `rem`-pixel positioning, so we can't reflow it —
// we shrink the whole canvas uniformly and let the panel keep its game-
// native dimensions. Recomputed on resize so dragging the window stays
// responsive.
function scaleCanvas() {
  const canvas = document.getElementById("game-canvas");
  if (!canvas) return;
  const sx = window.innerWidth / 1920;
  const sy = window.innerHeight / 1080;
  const s = Math.min(sx, sy, 1); // never upscale past 1:1
  canvas.style.transform = `scale(${s})`;
}
scaleCanvas();
window.addEventListener("resize", scaleCanvas);

// Deliberately NO React.StrictMode: production CS2 ships React in
// production mode, which renders each component exactly once. StrictMode
// double-renders in dev and can mask (or expose) bugs that only appear
// under single-render — and Coherent UI's reconciler has surprised us
// with quirks around child arrays. We want the harness to behave like
// the game so divergences are caught here, not after a CS2 round-trip.
const root = createRoot(document.getElementById("root")!);
root.render(<StorytellerToolbar />);
