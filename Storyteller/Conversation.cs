using System;
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
    //
    // Token accounting: each provider's response parser calls BuildTurn(...) which
    // accumulates into TotalUsage and fires TurnCompleted. Callers read TotalUsage
    // at the end of a run (AgentLoop surfaces it onto RunResult), or subscribe to
    // TurnCompleted for live per-hop updates.
    public abstract class Conversation
    {
        // Sum of every per-turn TokenUsage parsed during this conversation's
        // lifetime. Mutated only via BuildTurn so subclasses can't desync the
        // total from the per-turn values their parsers set on AssistantTurn.
        public TokenUsage TotalUsage { get; private set; }

        // Fires after every assistant turn the conversation parses, with that
        // turn's usage as the argument. Lets a UI panel show live token counts
        // without polling. Handlers run on the thread that completed the HTTP
        // request — marshal to the main thread before touching ECS / Unity state.
        public event Action<TokenUsage> TurnCompleted;

        public abstract Task<AssistantTurn> SendInitial(
            string system,
            string userPrompt,
            IReadOnlyList<ToolSchema> tools,
            CancellationToken ct);

        public abstract Task<AssistantTurn> SendToolResults(
            IReadOnlyList<ToolResult> results,
            CancellationToken ct);

        // Provider parsers call this once per response in lieu of constructing
        // an AssistantTurn directly. Keeps TotalUsage in sync and ensures the
        // TurnCompleted event fires consistently across providers.
        protected AssistantTurn BuildTurn(
            string textContent,
            IReadOnlyList<ToolCall> toolCalls,
            bool requiresToolResponse,
            TokenUsage usage)
        {
            TotalUsage += usage;
            TurnCompleted?.Invoke(usage);
            return new AssistantTurn
            {
                TextContent = textContent,
                ToolCalls = toolCalls,
                RequiresToolResponse = requiresToolResponse,
                Usage = usage,
            };
        }
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
