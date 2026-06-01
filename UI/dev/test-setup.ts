// Vitest setup file — runs once before each test file's first test.
// Brings in jest-dom's custom matchers (toBeInTheDocument, etc.) and
// resets the shared in-memory cs2/api registry so tests don't leak
// bindings into each other.

import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// jsdom doesn't provide ResizeObserver, which ChatScrollIndicator (and
// any future viewport-aware UI) depends on. A no-op stub is enough —
// tests don't drive layout-sensitive behavior, and the indicator's
// scroll/mutation paths still get exercised via direct event dispatch.
if (typeof globalThis.ResizeObserver === "undefined") {
  // @ts-expect-error - minimal stub, signature matches what we use.
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

// Note: we deliberately do NOT reset the cs2/api mock registry between
// tests. Production code imports binding references at module load
// (e.g. `export const canonTreeBinding = bindValue(...)`). Clearing the
// registry would orphan those imports — subsequent bindValue calls would
// return *new* bindings the components don't know about. Instead, the
// mock's bindValue updates the existing binding's value when called
// again, so per-test seeds replace whatever the previous test left.

afterEach(() => {
  cleanup();
});
