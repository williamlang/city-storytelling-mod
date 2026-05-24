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

// Pipe a diagnostic message to the C# mod log. Coherent UI has no
// user-accessible devtools, so console.log goes nowhere a player can
// reach. This trigger relays to PromptUISystem.OnUILog, which writes
// to Logs/CityStoryMod.log as `[UI] <message>`.
export function uiLog(message: string) {
  trigger(GROUP, "uiLog", message);
}
