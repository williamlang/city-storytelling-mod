using System.Collections.Generic;
using Colossal;
using Colossal.Localization;

namespace CityStoryMod
{
    public class Locale : IDictionarySource
    {
        readonly Dictionary<string, string> _entries;

        public Locale(Dictionary<string, string> entries) => _entries = entries;

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts) => _entries;

        public void Unload() { }

        public static Dictionary<string, string> EnglishEntries() => new()
        {
            ["Options.SECTION[CityStoryMod.CityStoryMod.Mod]"] = "Ghostwriter",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.IntervalMinutes]"] = "Auto-export interval (minutes)",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.IntervalMinutes]"] =
                "Wall-clock minutes between automatic exports. Set to 0 to disable interval exports; the hotkey still works.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.AutoSessionStartOnSaveLoad]"] = "Auto-start session on save load",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.AutoSessionStartOnSaveLoad]"] =
                "When on, the mod writes an open session stub into the city's sessions folder the moment a save is loaded. The next Claude conversation lands in a live session without needing /session-start. Skipped if a prior session is still open. Off by default — when off, the agent prompts you to run /session-start yourself.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.Provider]"] = "LLM provider",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.Provider]"] =
                "Which LLM service drives the in-game ghostwriter. Anthropic (API) uses a direct key against api.anthropic.com — paste a key below and you're done. Anthropic (Claude Code CLI) shells out to the `claude` command on your PATH and uses whatever credentials you've logged into the CLI with (including a Max subscription) — requires Claude Code to be installed and `claude --version` to work in the same shell CS2 was launched from. OpenAI / Gemini / Ollama use their respective HTTP APIs.",

            // Dropdown labels for each LlmProvider enum value. CS2 builds the locale
            // key for enum dropdowns as Options.<asset-id>.<ENUMTYPE-UPPER>[<Value>]
            // (e.g. Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[AnthropicAPI]).
            // Without these entries the dropdown shows the raw key as the label.
            ["Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[AnthropicAPI]"] = "Anthropic (API key)",
            ["Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[AnthropicCLI]"] = "Anthropic (Claude Code CLI)",
            ["Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[OpenAI]"] = "OpenAI",
            ["Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[Gemini]"] = "Google Gemini",
            ["Options.CityStoryMod.CityStoryMod.Mod.LLMPROVIDER[Ollama]"] = "Ollama (local)",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.ApiKey]"] = "API key",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.ApiKey]"] =
                "API key for the LLM provider selected above. Stored in this mod's settings file in plain text — anyone with access to your AppData can read it. Get a key from your provider's console (e.g. console.anthropic.com for Claude). Not required when Provider is Anthropic (Claude Code CLI) — the CLI carries its own credentials.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.Model]"] = "Model id",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.Model]"] =
                "Model id passed to the selected provider. Defaults to claude-opus-4-7 (matches Anthropic default). When you switch providers, paste a matching model id (e.g. gpt-5 for OpenAI, gemini-2.5-pro for Google, llama3.3 for Ollama).",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.OllamaBaseUrl]"] = "Ollama base URL",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.OllamaBaseUrl]"] =
                "Base URL of your Ollama server. Defaults to http://localhost:11434. Change if you run Ollama on a different host or port (e.g. a beefier home server). Only used when Provider is Ollama.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.OpenStoryFolder]"] = "Open story folder",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.OpenStoryFolder]"] =
                "Opens the current city's story folder in your file explorer (or the parent folder if no city has been exported yet). Read snapshots, canon, and session files there.",
        };
    }
}
