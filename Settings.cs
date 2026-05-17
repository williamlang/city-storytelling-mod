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

        public override void SetDefaults()
        {
            ExportEnabled = true;
        }
    }
}
