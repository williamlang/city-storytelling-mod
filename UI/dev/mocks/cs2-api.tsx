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
