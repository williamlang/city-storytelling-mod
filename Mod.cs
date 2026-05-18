using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using CityStoryMod.Systems;
using Game;
using Game.Modding;
using Game.SceneFlow;

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

            GameManager.instance.localizationManager.AddSource("en-US", new Locale(Locale.EnglishEntries()));

            updateSystem.UpdateBefore<ExportSystem>(SystemUpdatePhase.UIUpdate);

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
