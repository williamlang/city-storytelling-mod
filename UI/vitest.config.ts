import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import path from "path";

// Vitest config. Shares Vite's mock aliases for cs2/* so component tests
// see the same in-memory bindings the dev harness uses. JSDOM environment
// gives us a DOM for React Testing Library to render into.
//
// Test files live next to the components they cover: `Foo.test.tsx` is
// adjacent to `Foo.tsx`. Run with `npm test` (single-shot) or
// `npm test -- --watch` (watch mode).
export default defineConfig({
  resolve: {
    alias: {
      "cs2/api": path.resolve(__dirname, "dev/mocks/cs2-api.tsx"),
      "cs2/ui": path.resolve(__dirname, "dev/mocks/cs2-ui.tsx"),
      "cs2/modding": path.resolve(__dirname, "dev/mocks/cs2-modding.tsx"),
      "mod.json": path.resolve(__dirname, "mod.json"),
    },
  },
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./dev/test-setup.ts"],
    // Look for tests adjacent to source files.
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
    css: {
      // SCSS modules need to be parsed for `styles.foo` lookups in tests
      // to return *something* (defaults to the class name).
      modules: { classNameStrategy: "non-scoped" },
    },
  },
});
