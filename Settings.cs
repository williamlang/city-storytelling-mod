using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace CityStoryMod
{
    [FileLocation("ModsSettings/" + nameof(CityStoryMod) + "/" + nameof(CityStoryMod))]
    public class Settings : ModSetting
    {
        public Settings(IMod mod) : base(mod) { SetDefaults(); }

        public bool ExportEnabled { get; set; }

        [SettingsUISlider(min = 0, max = 60, step = 1, scalarMultiplier = 1, unit = "")]
        public int IntervalMinutes { get; set; }

        // Anthropic API credentials for the in-game storyteller. The key is stored
        // in CS2's settings file unencrypted — the description label spells this out
        // so the player can decide whether they're comfortable with that. Model
        // defaults to current best Claude (claude-opus-4-7); user can paste a newer
        // id without a mod update when Anthropic ships successors.
        [SettingsUITextInput]
        public string AnthropicApiKey { get; set; }

        [SettingsUITextInput]
        public string AnthropicModel { get; set; }

        public override void SetDefaults()
        {
            ExportEnabled = true;
            IntervalMinutes = 5;
            AnthropicApiKey = "";
            AnthropicModel = "claude-opus-4-7";
        }
    }
}
