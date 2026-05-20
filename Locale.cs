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

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.AnthropicApiKey]"] = "Anthropic API key",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.AnthropicApiKey]"] =
                "Your Anthropic API key, used by the in-game storyteller to call Claude. Stored in this mod's settings file in plain text — anyone with access to your AppData can read it. Get a key at console.anthropic.com.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.AnthropicModel]"] = "Claude model",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.AnthropicModel]"] =
                "Claude model id used for storyteller runs. Defaults to claude-opus-4-7 (current best). Paste a newer id here when Anthropic ships successors without waiting for a mod update.",
        };
    }
}
