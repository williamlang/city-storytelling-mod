import React from "react";
import { createRoot } from "react-dom/client";
import { StorytellerToolbar } from "../src/mods/promptWindow/PromptWindow";
import { seedSampleCity } from "./fixtures/sample-city";

// Seed mock bindings BEFORE rendering so the component sees realistic
// initial state. Edit dev/fixtures/sample-city.ts to change what the
// panel shows on first paint. Vite hot-reloads the fixture on save.
seedSampleCity();

const root = createRoot(document.getElementById("root")!);
root.render(
  <React.StrictMode>
    <StorytellerToolbar />
  </React.StrictMode>
);
