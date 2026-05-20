using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Routes a run to the right provider's client based on Settings.Provider.
    // Today only Anthropic is implemented; OpenAI / Gemini / Ollama land via
    // their own issues (#8 / #9 / #10) — picking them currently fails the run
    // cleanly with an explanatory message so the UX is obvious before the code
    // is there.
    public static class StorytellerRun
    {
        public static StorytellerDispatcher.RunFunc Build(string commandName, ILog log)
        {
            return async (CancellationToken ct) =>
            {
                Settings s = Mod.Settings;
                if (s == null) return RunResult.Failed("Settings not initialized.");

                string cityDir = Mod.LastExportedCityDir;
                if (string.IsNullOrEmpty(cityDir))
                    return RunResult.Failed("No exported city yet — trigger an export (Ctrl+Shift+E) first.");

                switch (s.Provider)
                {
                    case LlmProvider.Anthropic:
                        return await AnthropicClient.Run(s.ApiKey, s.Model, cityDir, commandName, log, ct);

                    case LlmProvider.OpenAI:
                        return RunResult.Failed("OpenAI provider not yet implemented (issue #8). Switch back to Anthropic.");
                    case LlmProvider.Gemini:
                        return RunResult.Failed("Gemini provider not yet implemented (issue #9). Switch back to Anthropic.");
                    case LlmProvider.Ollama:
                        return RunResult.Failed("Ollama provider not yet implemented (issue #10). Switch back to Anthropic.");

                    default:
                        return RunResult.Failed($"Unknown provider: {s.Provider}");
                }
            };
        }
    }
}
