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

        public bool WriteToSibling { get; set; }

        [SettingsUIDirectoryPicker]
        public string StorytellingRepoPath { get; set; }

        public override void SetDefaults()
        {
            ExportEnabled = true;
            IntervalMinutes = 5;
            WriteToSibling = false;
            StorytellingRepoPath = "";
        }
    }
}
