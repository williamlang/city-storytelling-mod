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
            ["Options.SECTION[CityStoryMod.CityStoryMod.Mod]"] = "City Story Mod",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.ExportEnabled]"] = "Enable exports",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.ExportEnabled]"] =
                "When on, writes a JSON snapshot on the hotkey (Ctrl+Shift+E) and at each interval tick.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.IntervalMinutes]"] = "Auto-export interval (minutes)",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.IntervalMinutes]"] =
                "Wall-clock minutes between automatic exports. Set to 0 to disable interval exports; the hotkey still works.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.Provider]"] = "LLM provider",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.Provider]"] =
                "Which LLM service drives the in-game storyteller. Anthropic (Claude) is the default and the only one implemented today — OpenAI, Gemini and Ollama are wired in via follow-up issues.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.ApiKey]"] = "API key",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.ApiKey]"] =
                "API key for the LLM provider selected above. Stored in this mod's settings file in plain text — anyone with access to your AppData can read it. Get a key from your provider's console (e.g. console.anthropic.com for Claude).",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.Model]"] = "Model id",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.Model]"] =
                "Model id passed to the selected provider. Defaults to claude-opus-4-7 (matches Anthropic default). When you switch providers, paste a matching model id (e.g. gpt-5 for OpenAI, gemini-2.5-pro for Google).",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.StorytellerStatus]"] = "Storyteller status",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.StorytellerStatus]"] =
                "Current state of the in-game storyteller. Updates when you open this panel — it doesn't tick while open, so re-open the panel to refresh.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.OpenStoryFolder]"] = "Open story folder",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.OpenStoryFolder]"] =
                "Opens the current city's story folder in your file explorer (or the parent folder if no city has been exported yet). Read snapshots, canon, and session files there.",
        };
    }
}
