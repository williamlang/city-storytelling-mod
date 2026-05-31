import { useState, useEffect } from "react";

// In-memory mock of the cs2/api module the production runtime injects.
// One global registry of named ValueBindings, addressable by group+name.
// useValue() subscribes to one of these; trigger() looks up special-cased
// handlers below (or falls through to console.log).
//
// This is enough fidelity to develop the storyteller UI without CS2 in
// the loop. It does NOT model CS2-specific quirks (Coherent UI rem
// scaling, missing font glyphs, SVG `currentColor` propagation bugs) —
// those are only visible in the real game.

type Listener<T> = (v: T) => void;

class LocalBinding<T> {
  value: T;
  listeners = new Set<Listener<T>>();

  constructor(value: T) { this.value = value; }

  subscribe(listener: Listener<T>) {
    this.listeners.add(listener);
    return { dispose: () => { this.listeners.delete(listener); } };
  }

  update(v: T) {
    this.value = v;
    this.listeners.forEach((l) => l(v));
  }
}

const registry = new Map<string, LocalBinding<any>>();

export function bindValue<T>(group: string, name: string, fallback: T): LocalBinding<T> {
  const key = `${group}.${name}`;
  const existing = registry.get(key);
  if (existing) {
    // Dev-mock semantics: subsequent bindValue calls for the same key update
    // the value. Diverges slightly from production (where bindValue is a
    // lookup), but it's how the seed fixtures and tests express "give this
    // binding this value right now." Production code rarely calls bindValue
    // twice for the same key, so the divergence is harmless in practice.
    existing.update(fallback);
    return existing;
  }
  const fresh = new LocalBinding<T>(fallback);
  registry.set(key, fresh);
  return fresh;
}

// Test-only: clear the mock registry so each test starts fresh. Not part
// of the production cs2/api shape — exported here for the Vitest setup.
export function _resetBindings() {
  registry.clear();
}

export function useValue<T>(binding: LocalBinding<T>): T {
  const [v, setV] = useState(binding.value);
  useEffect(() => {
    const sub = binding.subscribe(setV);
    return () => sub.dispose();
  }, [binding]);
  return v;
}

// Trigger handlers — special-cases for the few that have side effects we
// want to observe in dev. Everything else just logs. To exercise a new
// trigger in dev, add an arm here.
export function trigger(group: string, name: string, ...args: any[]) {
  console.log(`[trigger] ${group}.${name}`, args);

  if (group !== "CityStoryMod") return;

  if (name === "submitPrompt") {
    const prompt = args[0] as string;
    const messages = registry.get("CityStoryMod.messages");
    const isRunning = registry.get("CityStoryMod.isRunning");
    if (!messages || !isRunning) return;
    const current = JSON.parse(messages.value);
    current.push({ role: "user", text: prompt });
    messages.update(JSON.stringify(current));
    isRunning.update(true);
    // Fake a delayed assistant turn so the running-state cycle is visible.
    setTimeout(() => {
      current.push({
        role: "assistant",
        text: `Mock response to: "${prompt}". (In CS2 this would be the real LLM.)`,
      });
      messages.update(JSON.stringify(current));
      isRunning.update(false);
    }, 800);
    return;
  }

  if (name === "cancelRun") {
    registry.get("CityStoryMod.isRunning")?.update(false);
    return;
  }

  if (name === "clearMessages") {
    registry.get("CityStoryMod.messages")?.update("[]");
    return;
  }

  if (name === "setActiveEventsEnabled") {
    // Round-trip into the activeEventsEnabled binding so the toolbar
    // toggle visibly flips between on/off in the dev harness. The real
    // C# handler (PromptUISystem.OnSetActiveEventsEnabled) also writes
    // the value into the ModSetting; the mock has no equivalent to
    // persist, but the in-memory binding is enough for UI iteration.
    const enabled = !!args[0];
    registry.get("CityStoryMod.activeEventsEnabled")?.update(enabled);
    // Pretend a fresh interval just started: deadline = now + 2 min so
    // the countdown is visibly moving in the dev harness. When the
    // toggle flips off, drop to 0 to mirror the real PromptUISystem
    // behavior (binding emits 0 when ActiveEventsEnabled is false).
    // Carried as unix seconds to match the real binding's wire type.
    const nextFire = registry.get("CityStoryMod.nextEventAtUtcSec");
    nextFire?.update(enabled ? Math.floor(Date.now() / 1000) + 120 : 0);
    return;
  }
}

// Auto-renew the active-events deadline when it elapses, so the
// countdown harness keeps moving rather than parking at "ready"
// forever. Mirrors the real loop's behavior of advancing _last-
// GenerationUtc after each fire. Only ticks while a deadline is
// non-zero (i.e. active events is on) and not paused.
if (typeof window !== "undefined") {
  setInterval(() => {
    const paused = registry.get("CityStoryMod.activeEventsPaused");
    if (paused?.value) return;
    const b = registry.get("CityStoryMod.nextEventAtUtcSec");
    if (!b || !b.value) return;
    const nowSec = Math.floor(Date.now() / 1000);
    if (b.value > nowSec) return;
    b.update(nowSec + 120);
  }, 1000);

  // Dev convenience: window.__togglePaused() flips the paused flag in
  // the harness so the freeze behavior can be exercised without running
  // CS2. Mirrors the real loop's pause-edge advancement of the deadline:
  // on the unpause edge we push nextEventAtUtcSec forward by the paused
  // duration so the countdown picks up where it left off.
  let pausedAtMs: number | null = null;
  (window as any).__togglePaused = () => {
    const paused = registry.get("CityStoryMod.activeEventsPaused");
    if (!paused) return;
    const next = !paused.value;
    if (next) {
      pausedAtMs = Date.now();
    } else if (pausedAtMs != null) {
      const elapsedSec = Math.floor((Date.now() - pausedAtMs) / 1000);
      const b = registry.get("CityStoryMod.nextEventAtUtcSec");
      if (b && b.value) b.update(b.value + elapsedSec);
      pausedAtMs = null;
    }
    paused.update(next);
  };
}

// Unused in this mod's UI, included for API surface completeness.
export function bindTrigger() {
  return () => {};
}
export function bindTriggerWithArgs() {
  return () => {};
}
export function call<T>(_g: string, _n: string, ..._a: any[]): Promise<T> {
  return Promise.reject(new Error("call() is not mocked in dev"));
}
