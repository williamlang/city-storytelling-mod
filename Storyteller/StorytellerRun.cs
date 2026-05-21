using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Routes a run to the right Conversation impl based on Settings.Provider,
    // then hands it to the shared AgentLoop. Adding a new provider = adding a
    // new Conversation subclass and a switch arm here; no other code changes.
    // OpenAI / Gemini / Ollama land via #8 / #9 / #10 — picking them today
    // fails the run cleanly with a pointer to the issue.
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

                Conversation conv;
                switch (s.Provider)
                {
                    case LlmProvider.Anthropic:
                        conv = new AnthropicConversation(s.ApiKey, s.Model, log);
                        break;

                    case LlmProvider.OpenAI:
                        conv = new OpenAiConversation(s.ApiKey, s.Model, log);
                        break;

                    case LlmProvider.Gemini:
                        return RunResult.Failed("Gemini provider not yet implemented (issue #9). Switch back to Anthropic.");
                    case LlmProvider.Ollama:
                        return RunResult.Failed("Ollama provider not yet implemented (issue #10). Switch back to Anthropic.");

                    default:
                        return RunResult.Failed($"Unknown provider: {s.Provider}");
                }

                return await AgentLoop.RunAsync(conv, cityDir, commandName, log, ct);
            };
        }
    }
}
