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
            if (!(response["message"] is JObject message))
            {
                string preview = response.ToString(Formatting.None);
                if (preview.Length > 500) preview = preview.Substring(0, 500) + "…";
                throw new Exception($"Ollama response missing 'message' field. Raw: {preview}");
            }

            // Echo the assistant message back into history so subsequent
            // requests carry tool_calls intact.
            _messages.Add((JObject)message.DeepClone());

            string doneReason = (string)response["done_reason"];

            StringBuilder text = new StringBuilder();
            string content = (string)message["content"];
            if (!string.IsNullOrEmpty(content)) text.Append(content);

            List<ToolCall> calls = new List<ToolCall>();
            if (message["tool_calls"] is JArray toolCalls)
            {
                int synthIndex = 0;
                foreach (JToken tc in toolCalls)
                {
                    if (!(tc["function"] is JObject fn))
                    {
                        _log.Warn($"Ollama tool_call without 'function' field; skipping: {tc.ToString(Formatting.None)}");
                        continue;
                    }
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

            int contentLen = content?.Length ?? 0;
            _log.Info($"Ollama turn: done_reason={doneReason ?? "(none)"} content_len={contentLen} tool_calls={calls.Count}");

            // num_predict cap hit — output was truncated mid-stream. AgentLoop
            // would otherwise treat this as a completed turn and the model's
            // partial sentence becomes the final answer. Loud warn so debugging
            // "why did it cut off mid-paragraph" is fast.
            if (doneReason == "length")
                _log.Warn($"Ollama: response truncated at num_predict={MaxOutputTokens}. Bump it if outputs keep cutting off.");

            // Silent-tool-failure mode: tools were configured, the model didn't
            // call any, didn't produce text, and didn't return done_reason=stop.
            // Almost always: this model doesn't actually support function calling
            // (most small chat-tuned models). Cheaper to surface here than to
            // chase "the agent just stops doing anything" downstream.
            if (_toolsArray != null && _toolsArray.Count > 0 && calls.Count == 0 && contentLen == 0 && doneReason != "stop")
                _log.Warn($"Ollama: tools were sent but the model returned no tool_calls and no text (done_reason={doneReason ?? "(none)"}). Most likely the model doesn't support function-calling — try llama3.1 / qwen2.5 / mistral-nemo or another tool-capable model.");

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

            int toolCount = _toolsArray?.Count ?? 0;
            _log.Info($"Ollama POST {url} model={_model} messages={_messages.Count} tools={toolCount} num_predict={MaxOutputTokens}");

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    sw.Stop();
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Bare 404 on /api/chat with a "model not found" body is
                        // the common "you didn't pull the model" failure mode —
                        // the wrapped exception already echoes Ollama's body so
                        // the player sees the actionable hint.
                        _log.Warn($"Ollama API {(int)resp.StatusCode} after {sw.ElapsedMilliseconds}ms: {text}");
                        throw new Exception($"Ollama API {(int)resp.StatusCode}: {text}");
                    }
                    _log.Info($"Ollama responded in {sw.ElapsedMilliseconds}ms ({text.Length} bytes)");
                    return JObject.Parse(text);
                }
            }
            catch (HttpRequestException ex)
            {
                // Most likely: Ollama isn't running. Surface a clear hint
                // rather than the raw network error.
                sw.Stop();
                _log.Warn($"Ollama unreachable after {sw.ElapsedMilliseconds}ms: {ex.Message}");
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
