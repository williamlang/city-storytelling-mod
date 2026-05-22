using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Conversation implementation for OpenAI's Chat Completions API. Talks to
    // /v1/chat/completions with Bearer auth.
    //
    // Wire differences vs. Anthropic worth flagging:
    //   * System lives as a role=system message inside the messages array,
    //     not as a separate top-level field.
    //   * Tools are wrapped { type: "function", function: { ... } } and the
    //     JSON Schema sits under `function.parameters` (not `input_schema`).
    //   * The model returns tool calls in choices[0].message.tool_calls, and
    //     each tool call's `arguments` field is a JSON-encoded STRING (not a
    //     JObject). We JObject.Parse it back into structured input for the
    //     provider-agnostic ToolCall.
    //   * Tool results come back as N separate role=tool messages (one per
    //     result), unlike Anthropic's single user message with N blocks.
    //   * Caching is automatic on the server side — no cache_control markers.
    //     Hit info surfaces as usage.prompt_tokens_details.cached_tokens. To
    //     keep TokenUsage semantics consistent with Anthropic (InputTokens =
    //     fresh tokens, CacheReadTokens = served-from-cache), we subtract
    //     cached from prompt_tokens; CacheWriteTokens stays 0 since OpenAI
    //     doesn't surface a separate write count.
    public class OpenAiConversation : Conversation
    {
        const string Endpoint = "https://api.openai.com/v1/chat/completions";
        const int MaxCompletionTokens = 8192;

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        readonly string _apiKey;
        readonly string _model;
        readonly ILog _log;

        readonly JArray _messages = new JArray();
        JArray _toolsArray;

        public OpenAiConversation(string apiKey, string model, ILog log)
        {
            _apiKey = apiKey;
            _model = model;
            _log = log;
        }

        public override async Task<AssistantTurn> SendInitial(
            string system,
            string userPrompt,
            IReadOnlyList<ToolSchema> tools,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new Exception("API key is empty — paste a key into Options first.");
            if (string.IsNullOrWhiteSpace(_model))
                throw new Exception("Model id is empty — paste one into Options first.");

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
                _messages.Add(new JObject {
                    ["role"] = "tool",
                    ["tool_call_id"] = r.ToolUseId,
                    ["content"] = r.Content,
                });
            }
            return await PostAndCapture(ct);
        }

        async Task<AssistantTurn> PostAndCapture(CancellationToken ct)
        {
            JObject response = await Post(ct);
            JObject choice = (JObject)((JArray)response["choices"])[0];
            JObject message = (JObject)choice["message"];

            // Echo the full assistant message back into our history so subsequent
            // requests carry the tool_calls intact (OpenAI requires this).
            _messages.Add((JObject)message.DeepClone());

            StringBuilder text = new StringBuilder();
            string content = (string)message["content"];
            if (!string.IsNullOrEmpty(content)) text.Append(content);

            List<ToolCall> calls = new List<ToolCall>();
            if (message["tool_calls"] is JArray toolCalls)
            {
                foreach (JToken tc in toolCalls)
                {
                    string id = (string)tc["id"];
                    JObject fn = (JObject)tc["function"];
                    string name = (string)fn["name"];
                    string argsJson = (string)fn["arguments"];
                    // arguments is a STRING containing JSON; parse back into a
                    // structured JObject for ToolExecutor to consume.
                    JObject input;
                    try
                    {
                        input = string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Failed to parse tool arguments for {name}: {ex.Message}. Raw: {argsJson}");
                        input = new JObject();
                    }
                    calls.Add(new ToolCall { Id = id, Name = name, Input = input });
                }
            }

            string finishReason = (string)choice["finish_reason"];
            return BuildTurn(
                text.ToString(),
                calls,
                finishReason == "tool_calls" && calls.Count > 0,
                ParseUsage(response["usage"] as JObject));
        }

        async Task<JObject> Post(CancellationToken ct)
        {
            JObject body = new JObject
            {
                ["model"] = _model,
                ["max_completion_tokens"] = MaxCompletionTokens,
                ["messages"] = _messages,
                ["tools"] = _toolsArray,
            };

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"OpenAI API {(int)resp.StatusCode}: {text}");
                return JObject.Parse(text);
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

        static TokenUsage ParseUsage(JObject u)
        {
            if (u == null) return default;
            int prompt = u["prompt_tokens"]?.Value<int>() ?? 0;
            int cached = u["prompt_tokens_details"]?["cached_tokens"]?.Value<int>() ?? 0;
            return new TokenUsage
            {
                InputTokens = Math.Max(0, prompt - cached),
                CacheReadTokens = cached,
                CacheWriteTokens = 0, // OpenAI doesn't surface a separate write count
                OutputTokens = u["completion_tokens"]?.Value<int>() ?? 0,
            };
        }
    }
}
