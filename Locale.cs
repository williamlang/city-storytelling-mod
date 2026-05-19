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

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.WriteToSibling]"] = "Also write to storytelling repo",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.WriteToSibling]"] =
                "When on, snapshots are also written to <StorytellingRepoPath>/imports/ alongside the default ModsData folder. The default location keeps a complete local history.",

            ["Options.OPTION[CityStoryMod.CityStoryMod.Mod.Settings.StorytellingRepoPath]"] = "Storytelling repo path",
            ["Options.OPTION_DESCRIPTION[CityStoryMod.CityStoryMod.Mod.Settings.StorytellingRepoPath]"] =
                "Local path to the city-storytelling repo root. The mod writes into its 'imports' subfolder (created if missing). Only used when 'Also write to storytelling repo' is on.",
        };
    }
}
