using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;

namespace CityStoryMod
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager
            .GetLogger(nameof(CityStoryMod))
            .SetShowsErrorsInUI(true);

        public static Settings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Settings = new Settings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(CityStoryMod), Settings, new Settings(this));

            Log.Info("CityStoryMod loaded.");
        }

        public void OnDispose()
        {
            Settings?.UnregisterInOptionsUI();
            Settings = null;

            Log.Info("CityStoryMod disposed.");
        }
    }
}
