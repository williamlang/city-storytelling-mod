import { bindValue, trigger } from "cs2/api";

// One-time binding handles, mirroring the four ValueBindings registered by
// Systems/PromptUISystem.cs in the "CityStoryMod" group. Created at module
// load (CS2 wires them up once the C# system registers) and shared across
// all component instances via the useValue hook.
const GROUP = "CityStoryMod";

export const messagesBinding = bindValue<string>(GROUP, "messages", "[]");
export const isRunningBinding = bindValue<boolean>(GROUP, "isRunning", false);
export const tokenSummaryBinding = bindValue<string>(GROUP, "tokenSummary", "");
export const lastErrorBinding = bindValue<string>(GROUP, "lastError", "");
export const availableCommandsBinding = bindValue<string>(GROUP, "availableCommands", "[]");
export const canonTreeBinding = bindValue<string>(GROUP, "canonTree", "{}");
export const cartoExportingBinding = bindValue<boolean>(GROUP, "cartoExporting", false);
export const cartoAvailableBinding = bindValue<boolean>(GROUP, "cartoAvailable", false);
export const activeEventsEnabledBinding = bindValue<boolean>(GROUP, "activeEventsEnabled", false);
// Unix-seconds timestamp of the next eligible autonomous /story-driven
// fire. 0 when active events is off. UI computes a local countdown
// against wall-clock time. Carried as seconds (not ms) because CS2's
// ValueBinding type system has no writer for Int64.
export const nextEventAtUtcSecBinding = bindValue<number>(GROUP, "nextEventAtUtcSec", 0);
// True while the autonomous loop is frozen — sim paused or game not in
// an active session. UI freezes the displayed countdown to whatever
// "remaining" was at the pause→true edge, and skips its 1Hz tick.
export const activeEventsPausedBinding = bindValue<boolean>(GROUP, "activeEventsPaused", false);

// JSON-serialized list of currently-open story events (status: open in
// frontmatter), sorted by deadline ascending. Drives the inbox strip
// rendered above the chat/canon body. Refreshes on the same canon-
// watcher debounce as canonTree, so /events-resolve closing entries
// updates the inbox automatically.
export const openEventsBinding = bindValue<string>(GROUP, "openEvents", "[]");

export interface OpenEvent {
  path: string;              // relative to cityDir, e.g. "events/2026-03-14-foo.md"
  title: string;
  date: string;              // in-world ISO date the event opened
  in_world_deadline: string; // in-world ISO date the event auto-resolves
}

// Wire-format mirror of CityStoryMod.Systems.ChatMessage (lowercase fields
// match the JsonConvert.SerializeObject output from C#). Tool calls / tool
// results are intentionally NOT in the wire — the C# side drops them so the
// chat shows only the prose conversation.
export interface ChatMessage {
  role: "user" | "assistant";
  text: string;
}

// Wire-format mirror of CityStoryMod.Systems.SlashCommand.
export interface SlashCommand {
  name: string;        // filename stem, e.g. "story-driven"
  description: string; // frontmatter `description:` field, "" when missing
}

// Wire-format mirror of ScanCanonTree() output: { subdir → entries[] }.
// Subdir keys correspond to canon/ characters/ companies/ places/ factions/
// events/ stories/ sessions/ secrets/. Empty subdirs are dropped, so the
// caller can `Object.keys(tree)` and render headers for whatever's there.
// File content is eager-loaded server-side (capped at 20KB) so opening a
// modal is instant and many can be open simultaneously without per-modal
// async fetches.
export interface CanonEntry {
  name: string;    // filename stem
  path: string;    // relative path under cityDir, e.g. "characters/foo.md"
  content: string; // markdown source (already capped + truncated on C# side)
}
export type CanonTree = Record<string, CanonEntry[]>;

export function submitPrompt(prompt: string) {
  trigger(GROUP, "submitPrompt", prompt);
}

export function cancelRun() {
  trigger(GROUP, "cancelRun");
}

export function clearMessages() {
  trigger(GROUP, "clearMessages");
}

export function refreshGeography() {
  trigger(GROUP, "refreshGeography");
}

export function setActiveEventsEnabled(enabled: boolean) {
  trigger(GROUP, "setActiveEventsEnabled", enabled);
}

// Fly the in-game camera to a coordinate the storyteller mentioned in chat.
// Coordinates are in the agent's recentered-meters frame (the same pairs that
// appear in carto/processed/*.md and the agent's prose). Sent as a single
// "x,y" string — mirrors submitPrompt and sidesteps CS2's multi-arg / Int64
// binding limitations. PromptUISystem.OnMapGoto parses + translates to world
// space and hands off to CameraNavSystem.
export function mapGoto(x: number, y: number) {
  trigger(GROUP, "mapGoto", `${x},${y}`);
}

// Pipe a diagnostic message to the C# mod log. Coherent UI has no
// user-accessible devtools, so console.log goes nowhere a player can
// reach. This trigger relays to PromptUISystem.OnUILog, which writes
// to Logs/CityStoryMod.log as `[UI] <message>`.
export function uiLog(message: string) {
  trigger(GROUP, "uiLog", message);
}
