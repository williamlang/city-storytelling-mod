using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Conversation implementation for Ollama's native /api/chat endpoint.
    // Auth: none (localhost or trusted LAN). Endpoint comes from settings so
    // the player can point at a remote box if they're running Ollama there.
    //
    // Wire-format notes vs. OpenAI (the closest cousin):
    //   * Top-level `message` instead of `choices[0].message`.
    //   * `tool_calls[].function.arguments` is a structured JObject (unlike
    //     OpenAI's JSON-encoded string).
    //   * Tool calls historically don't carry stable ids — we read `id` if the
    //     model includes one, otherwise synthesize like Gemini and track the
    //     name internally so SendToolResults can echo correctly.
    //   * Field is `done_reason` (with value "stop" or "tool_calls" on capable
    //     models) instead of `finish_reason`. We also fall back to the presence
    //     of tool_calls in the message as the decision signal.
    //   * Usage is `prompt_eval_count` / `eval_count`. No cached-token field —
    //     local KV cache exists in Ollama internally but isn't surfaced as
    //     wire-level usage detail.
    //
    // Cold-start caveat: the first request after model load can hang for 10–30s
    // before any tokens flow. Dispatcher status will read "Running…" for longer
    // than usual; not an error.
    public class OllamaConversation : Conversation
    {
        const int MaxOutputTokens = 8192;

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        readonly string _baseUrl;
        readonly string _model;
        readonly ILog _log;

        readonly JArray _messages = new JArray();
        JArray _toolsArray;

        // Tracks function name behind each synthesized tool-call id, used only
        // when Ollama's response doesn't carry ids. Cleared after each turn.
        readonly Dictionary<string, string> _callNameById = new Dictionary<string, string>();

        public OllamaConversation(string baseUrl, string model, ILog log)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _model = model;
            _log = log;
        }

        public override async Task<AssistantTurn> SendInitial(
            string system,
            string userPrompt,
            IReadOnlyList<ToolSchema> tools,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                throw new Exception("Ollama base URL is empty — set Options → Ollama base URL.");
            if (string.IsNullOrWhiteSpace(_model))
                throw new Exception("Model id is empty — paste one into Options first (e.g. llama3.3).");

            _toolsArray = SerializeTools(tools);
            _messages.Add(new JObject { ["role"] = "system", ["content"] = system });
            _messages.Add(new JObject { ["role"] = "user", ["content"] = userPrompt });

            return await PostAndCapture(ct);
        }

        public override async Task<AssistantTurn> SendToolResults(
            IReadOnlyList<ToolResult> results,
            CancellationToken ct)
        {
            foreach (ToolResult r in results)
            {
                JObject msg = new JObject
                {
                    ["role"] = "tool",
                    ["content"] = r.Content,
                };
                // Include tool_call_id when we have a real id from the model;
                // for synthesized ids (Ollama didn't return one), Ollama matches
                // positionally and the id can be omitted.
                if (!r.ToolUseId.StartsWith("ollama-synth-", StringComparison.Ordinal))
                    msg["tool_call_id"] = r.ToolUseId;
                _messages.Add(msg);
            }
            _callNameById.Clear();
            return await PostAndCapture(ct);
        }

        async Task<AssistantTurn> PostAndCapture(CancellationToken ct)
        {
            JObject response = await Post(ct);
            JObject message = (JObject)response["message"];

            // Echo the assistant message back into history so subsequent
            // requests carry tool_calls intact.
            _messages.Add((JObject)message.DeepClone());

            StringBuilder text = new StringBuilder();
            string content = (string)message["content"];
            if (!string.IsNullOrEmpty(content)) text.Append(content);

            List<ToolCall> calls = new List<ToolCall>();
            if (message["tool_calls"] is JArray toolCalls)
            {
                int synthIndex = 0;
                foreach (JToken tc in toolCalls)
                {
                    JObject fn = (JObject)tc["function"];
                    string name = (string)fn["name"];
                    // Ollama returns arguments as a structured object on
                    // /api/chat — no JSON-string parse required.
                    JObject args = fn["arguments"] as JObject ?? new JObject();
                    string id = (string)tc["id"];
                    if (string.IsNullOrEmpty(id))
                    {
                        id = $"ollama-synth-{synthIndex}-{name}";
                        synthIndex++;
                        _callNameById[id] = name;
                    }
                    calls.Add(new ToolCall { Id = id, Name = name, Input = (JObject)args.DeepClone() });
                }
            }

            // done_reason is "stop" when finished, "tool_calls" when handing off
            // to tools on capable models. Some models / versions don't set
            // done_reason explicitly when tool_calls are present, so the
            // presence of tool_calls is the durable signal.
            return BuildTurn(
                text.ToString(),
                calls,
                calls.Count > 0,
                ParseUsage(response));
        }

        async Task<JObject> Post(CancellationToken ct)
        {
            string url = $"{_baseUrl}/api/chat";
            JObject body = new JObject
            {
                ["model"] = _model,
                ["messages"] = _messages,
                ["tools"] = _toolsArray,
                ["stream"] = false,
                ["options"] = new JObject
                {
                    ["num_predict"] = MaxOutputTokens,
                },
            };

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            try
            {
                using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"Ollama API {(int)resp.StatusCode}: {text}");
                    return JObject.Parse(text);
                }
            }
            catch (HttpRequestException ex)
            {
                // Most likely: Ollama isn't running. Surface a clear hint
                // rather than the raw network error.
                throw new Exception($"Could not reach Ollama at {url}. Is the server running? ({ex.Message})");
            }
        }

        static JArray SerializeTools(IReadOnlyList<ToolSchema> tools)
        {
            JArray result = new JArray();
            foreach (ToolSchema t in tools)
            {
                result.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.InputSchema.DeepClone(),
                    },
                });
            }
            return result;
        }

        // Ollama puts token counts at the top of the response, not nested under
        // `usage`. No cached-token field — local KV cache is internal.
        static TokenUsage ParseUsage(JObject response)
        {
            return new TokenUsage
            {
                InputTokens = response["prompt_eval_count"]?.Value<int>() ?? 0,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                OutputTokens = response["eval_count"]?.Value<int>() ?? 0,
            };
        }
    }
}
