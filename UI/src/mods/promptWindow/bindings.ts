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

// Wire-format mirror of CityStoryMod.Systems.ChatMessage (lowercase fields
// match the JsonConvert.SerializeObject output from C#).
export interface ChatMessage {
  role: "user" | "assistant" | "tool";
  text: string;
  toolCalls?: string;
  isError?: boolean;
}

export function submitPrompt(prompt: string) {
  trigger(GROUP, "submitPrompt", prompt);
}

export function cancelRun() {
  trigger(GROUP, "cancelRun");
}

export function clearMessages() {
  trigger(GROUP, "clearMessages");
}
