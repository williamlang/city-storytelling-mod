// Vitest setup file — runs once before each test file's first test.
// Brings in jest-dom's custom matchers (toBeInTheDocument, etc.) and
// resets the shared in-memory cs2/api registry so tests don't leak
// bindings into each other.

import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

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
