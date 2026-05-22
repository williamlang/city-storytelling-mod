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
    // Conversation implementation for Anthropic's Messages API. Holds the JArray
    // of {role, content} turns internally + a sliding window of cache_control
    // marker indices so the shared AgentLoop never sees Anthropic-specific wire
    // detail.
    //
    // Caching: system prompt always carries an ephemeral cache_control marker
    // (caches CLAUDE.md across back-to-back runs within 5-minute TTL). Up to 3
    // additional markers float on the most recent tool_result messages so each
    // hop reads cache from prior hops' tails. Oldest marker evicted when the
    // window would exceed Anthropic's 4-marker per-request limit.
    public class AnthropicConversation : Conversation
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string AnthropicVersion = "2023-06-01";
        const int MaxTokens = 8192;
        const int MaxMessageCacheMarkers = 3;

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        readonly string _apiKey;
        readonly string _model;
        readonly ILog _log;

        // Wire-format state. Built up across SendInitial/SendToolResults calls.
        JArray _systemContent;
        readonly JArray _messages = new JArray();
        readonly List<int> _markerIndices = new List<int>();
        JArray _toolsArray; // serialized tool definitions, built once on SendInitial

        public AnthropicConversation(string apiKey, string model, ILog log)
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

            _systemContent = new JArray {
                new JObject {
                    ["type"] = "text",
                    ["text"] = system,
                    ["cache_control"] = new JObject { ["type"] = "ephemeral" },
                },
            };
            _toolsArray = SerializeTools(tools);
            _messages.Add(new JObject { ["role"] = "user", ["content"] = userPrompt });

            JObject response = await Post(ct);
            return CaptureAssistantTurn(response);
        }

        public override async Task<AssistantTurn> SendToolResults(
            IReadOnlyList<ToolResult> results,
            CancellationToken ct)
        {
            JArray toolResultBlocks = new JArray();
            foreach (ToolResult r in results)
            {
                toolResultBlocks.Add(new JObject {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = r.ToolUseId,
                    ["content"] = r.Content,
                    ["is_error"] = r.IsError,
                });
            }
            _messages.Add(new JObject { ["role"] = "user", ["content"] = toolResultBlocks });
            AddCacheMarkerToLatest();

            JObject response = await Post(ct);
            return CaptureAssistantTurn(response);
        }

        // Append the assistant content into _messages (DeepCloned so the response
        // tree we read for tool calls is detached from the messages tree we send
        // next), then build the provider-agnostic AssistantTurn for AgentLoop.
        AssistantTurn CaptureAssistantTurn(JObject response)
        {
            string stopReason = (string)response["stop_reason"];
            JArray content = (JArray)response["content"];
            _messages.Add(new JObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

            StringBuilder text = new StringBuilder();
            List<ToolCall> calls = new List<ToolCall>();
            foreach (JToken block in content)
            {
                string type = (string)block["type"];
                if (type == "text")
                {
                    text.Append((string)block["text"]);
                }
                else if (type == "tool_use")
                {
                    calls.Add(new ToolCall {
                        Id = (string)block["id"],
                        Name = (string)block["name"],
                        Input = (JObject)block["input"],
                    });
                }
            }

            return BuildTurn(
                text.ToString(),
                calls,
                stopReason == "tool_use" && calls.Count > 0,
                ParseUsage(response["usage"] as JObject));
        }

        async Task<JObject> Post(CancellationToken ct)
        {
            JObject body = new JObject
            {
                ["model"] = _model,
                ["max_tokens"] = MaxTokens,
                ["system"] = _systemContent,
                ["messages"] = _messages,
                ["tools"] = _toolsArray,
            };

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Anthropic API {(int)resp.StatusCode}: {text}");
                return JObject.Parse(text);
            }
        }

        // Adds cache_control to the last content block of the most recently
        // appended message, then drops the oldest tracked marker if we've exceeded
        // the per-request cap.
        void AddCacheMarkerToLatest()
        {
            int idx = _messages.Count - 1;
            JObject msg = (JObject)_messages[idx];
            JArray content = msg["content"] as JArray;
            if (content == null || content.Count == 0) return; // string-content messages aren't markable
            JObject lastBlock = content[content.Count - 1] as JObject;
            if (lastBlock == null) return;
            lastBlock["cache_control"] = new JObject { ["type"] = "ephemeral" };
            _markerIndices.Add(idx);

            while (_markerIndices.Count > MaxMessageCacheMarkers)
            {
                int oldestIdx = _markerIndices[0];
                _markerIndices.RemoveAt(0);
                JObject oldMsg = (JObject)_messages[oldestIdx];
                JArray oldContent = oldMsg["content"] as JArray;
                if (oldContent == null || oldContent.Count == 0) continue;
                JObject oldLast = oldContent[oldContent.Count - 1] as JObject;
                oldLast?.Remove("cache_control");
            }
        }

        static JArray SerializeTools(IReadOnlyList<ToolSchema> tools)
        {
            JArray result = new JArray();
            foreach (ToolSchema t in tools)
            {
                result.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema.DeepClone(),
                });
            }
            return result;
        }

        static TokenUsage ParseUsage(JObject u)
        {
            if (u == null) return default;
            return new TokenUsage
            {
                InputTokens = u["input_tokens"]?.Value<int>() ?? 0,
                CacheReadTokens = u["cache_read_input_tokens"]?.Value<int>() ?? 0,
                CacheWriteTokens = u["cache_creation_input_tokens"]?.Value<int>() ?? 0,
                OutputTokens = u["output_tokens"]?.Value<int>() ?? 0,
            };
        }
    }
}
