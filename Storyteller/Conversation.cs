using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Provider-agnostic conversation state. Each LLM provider (Anthropic, OpenAI,
    // Gemini, Ollama) subclasses this and maintains its own native wire format
    // (Anthropic's JArray of {role, content}, OpenAI's tool_calls field, Gemini's
    // contents+parts, etc.). The shared AgentLoop talks to providers through this
    // abstraction without knowing the differences.
    //
    // Lifecycle: instantiate per run. Call SendInitial once with the system prompt
    // and the first user turn. Then loop SendToolResults until AssistantTurn says
    // the model is done (no tool calls or stop reason indicates end_turn).
    public abstract class Conversation
    {
        public abstract Task<AssistantTurn> SendInitial(
            string system,
            string userPrompt,
            IReadOnlyList<ToolSchema> tools,
            CancellationToken ct);

        public abstract Task<AssistantTurn> SendToolResults(
            IReadOnlyList<ToolResult> results,
            CancellationToken ct);
    }

    public class AssistantTurn
    {
        // Any free text the model emitted alongside (or instead of) tool calls.
        public string TextContent;

        // Tool calls the model wants executed. Empty when the model is done.
        public IReadOnlyList<ToolCall> ToolCalls;

        // True when the loop should execute ToolCalls and call SendToolResults.
        // False when the model is done (either text-only response or end_turn).
        public bool RequiresToolResponse;

        public TokenUsage Usage;
    }

    public class ToolCall
    {
        public string Id;
        public string Name;
        public JObject Input;
    }

    public class ToolResult
    {
        public string ToolUseId;
        public string Content;
        public bool IsError;
    }

    public struct TokenUsage
    {
        public int InputTokens;
        public int CacheReadTokens;
        public int CacheWriteTokens;
        public int OutputTokens;

        public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new TokenUsage
        {
            InputTokens = a.InputTokens + b.InputTokens,
            CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
            CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
        };
    }

    public class ToolSchema
    {
        public string Name;
        public string Description;
        public JObject InputSchema; // JSON Schema describing the tool's input
    }
}
