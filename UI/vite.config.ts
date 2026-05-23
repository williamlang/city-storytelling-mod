import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "path";

// Vite config for out-of-CS2 React iteration. Runs the storyteller panel
// in a regular browser tab against mocked cs2/* modules, so layout + state
// + interaction logic can be developed without the edit-quit-build-launch
// cycle.
//
// Caveats: mocks lie. Coherent UI quirks (e.g. SVG `currentColor` not
// propagating, font fallbacks tofu-ing unicode glyphs) only show up in
// the real CS2 runtime. Final visual sign-off still requires the game.
//
// Usage: `npm run dev:web` from UI/. Opens at http://localhost:5173.
export default defineConfig({
  // dev/ holds the harness entry (index.html, main.tsx, mocks/). The
  // actual component source lives in src/ and is imported from there.
  root: path.resolve(__dirname, "dev"),
  resolve: {
    alias: {
      // Redirect cs2/* externals to local mocks. Same import shape as in
      // production, just different implementation.
      "cs2/api": path.resolve(__dirname, "dev/mocks/cs2-api.tsx"),
      "cs2/ui": path.resolve(__dirname, "dev/mocks/cs2-ui.tsx"),
      "cs2/modding": path.resolve(__dirname, "dev/mocks/cs2-modding.tsx"),
      "mod.json": path.resolve(__dirname, "mod.json"),
    },
  },
  plugins: [react()],
  server: {
    port: 5173,
    open: false,
  },
});
