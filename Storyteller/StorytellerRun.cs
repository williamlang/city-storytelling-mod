using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;

namespace CityStoryMod.Storyteller
{
    // Routes a run to the right Conversation impl based on Settings.Provider,
    // then hands it to AgentLoop. Two entry points:
    //
    //   Build(commandName)  — loads .claude/commands/<name>.md as the prompt
    //                         (used when the prompt panel's command dropdown
    //                         picks a slash command)
    //   BuildFreeForm(text) — uses the given text directly as the user prompt
    //                         (used by the in-game prompt panel)
    //
    // Both wire the Conversation through Mod.Storyteller.AttachConversation so
    // dispatcher event subscribers (e.g. PromptUISystem) see the streaming
    // turns without needing direct access to the Conversation.
    public static class StorytellerRun
    {
        public static StorytellerDispatcher.RunFunc Build(string commandName, ILog log)
        {
            return (CancellationToken ct) => RunWithProvider(
                cityDir => AgentLoop.RunCommandAsync(BuildConversation(log), cityDir, commandName, log, ct),
                cliRun: (cityDir) => ClaudeCliRunner.RunAsync(cityDir, commandName, log, ct),
                log);
        }

        public static StorytellerDispatcher.RunFunc BuildFreeForm(string userPrompt, ILog log)
        {
            return (CancellationToken ct) => RunWithProvider(
                cityDir => AgentLoop.RunPromptAsync(BuildConversation(log), cityDir, userPrompt, log, ct),
                // CLI runner takes the prompt as the -p argument verbatim.
                cliRun: (cityDir) => ClaudeCliRunner.RunPromptAsync(cityDir, userPrompt, log, ct),
                log);
        }

        // Resolves city dir + dispatches to either the API agent loop or the
        // CLI runner based on Settings.Provider. Both branches return a
        // RunResult; the API branch also attaches the Conversation to the
        // dispatcher so its events stream out to UI subscribers.
        static async Task<RunResult> RunWithProvider(
            System.Func<string, Task<RunResult>> apiRun,
            System.Func<string, Task<RunResult>> cliRun,
            ILog log)
        {
            Settings s = Mod.Settings;
            if (s == null) return RunResult.Failed("Settings not initialized.");

            string cityDir = Mod.LastExportedCityDir;
            if (string.IsNullOrEmpty(cityDir))
                return RunResult.Failed("No exported city yet — trigger an export (Ctrl+Shift+E) first.");

            if (s.Provider == LlmProvider.AnthropicCLI)
                return await cliRun(cityDir);

            return await apiRun(cityDir);
        }

        // Picks the right Conversation subclass for the configured API provider
        // and registers it with the dispatcher so streaming events flow out to
        // UI subscribers. Throws on unknown provider — callers should have
        // routed CLI separately before reaching this.
        static Conversation BuildConversation(ILog log)
        {
            Settings s = Mod.Settings;
            Conversation conv;
            switch (s.Provider)
            {
                case LlmProvider.AnthropicAPI:
                    conv = new AnthropicConversation(s.ApiKey, s.Model, log);
                    break;
                case LlmProvider.OpenAI:
                    conv = new OpenAiConversation(s.ApiKey, s.Model, log);
                    break;
                case LlmProvider.Gemini:
                    conv = new GeminiConversation(s.ApiKey, s.Model, log);
                    break;
                case LlmProvider.Ollama:
                    conv = new OllamaConversation(s.OllamaBaseUrl, s.Model, log);
                    break;
                default:
                    throw new System.InvalidOperationException($"Unknown provider: {s.Provider}");
            }
            return Mod.Storyteller?.AttachConversation(conv) ?? conv;
        }
    }
}
