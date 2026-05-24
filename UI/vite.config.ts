import { defineConfig, Plugin } from "vite";
import react from "@vitejs/plugin-react";
import path from "path";

// Re-substitute __BUILD_TIME__ on every transform so the dev harness
// shows the actual freshness of the running bundle, not the timestamp
// from when Vite first booted. Vite's `define` evaluates exactly once
// at config load and stays frozen — useless for "is this HMR update
// live yet" debugging. A transform plugin runs every time a file is
// re-processed, which happens on every save.
const dynamicBuildTime = (): Plugin => ({
  name: "ghostwriter-dynamic-build-time",
  enforce: "pre",
  transform(code, id) {
    if (id.includes("node_modules")) return null;
    if (!/\.tsx?$/.test(id)) return null;
    if (!code.includes("__BUILD_TIME__")) return null;
    const now =
      new Date().toISOString().slice(0, 16).replace("T", " ") + " (dev)";
    return { code: code.replace(/__BUILD_TIME__/g, JSON.stringify(now)) };
  },
});

// Vite config for out-of-CS2 React iteration. Runs the storyteller panel
// in a regular browser tab against mocked cs2/* modules, so layout + state
// + interaction logic can be developed without the edit-quit-build-launch
// cycle.
//
// Caveats: mocks lie. Coherent UI quirks (e.g. SVG `currentColor` not
// propagating, font fallbacks tofu-ing unicode glyphs, multi-text-node
// Fragment children rendering out of source order) only show up in the
// real CS2 runtime. Final visual sign-off still requires the game.
//
// We tried serving React in production mode (NODE_ENV=production via
// `define`) to match the game more closely, but the Vite React plugin
// keeps emitting jsxDEV() calls in dev mode while React's production
// bundle doesn't export jsxDEV — `Uncaught TypeError: jsxDEV is not a
// function` on first render. The two halves fight, and the only clean
// fix is to also force the JSX transform to production, which removes
// the better error overlays that make the harness useful in the first
// place. Net: dev React in the harness, real-browser tests are the
// place to chase closer-to-Coherent rendering parity.
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
  plugins: [dynamicBuildTime(), react()],
  server: {
    port: 5173,
    open: false,
  },
});
