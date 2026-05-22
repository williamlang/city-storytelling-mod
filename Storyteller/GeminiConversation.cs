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
    // Conversation implementation for Google's Gemini API (AI Studio variant).
    // Endpoint: generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
    // Auth: API key in the query string (the unusual AI Studio style; Vertex AI
    // uses a different auth path and is out of scope).
    //
    // Wire differences vs. Anthropic / OpenAI worth flagging:
    //   * System prompt lives in a top-level `systemInstruction` field, not as
    //     a message — its shape is `{parts: [{text: "..."}]}`.
    //   * Contents use roles `user` and `model` (not `assistant`). Every turn
    //     is `{role, parts: [...]}` where parts can be text, functionCall, or
    //     functionResponse blocks.
    //   * Tools wrap as a one-element array containing all declarations:
    //     `tools: [{functionDeclarations: [{name, description, parameters}]}]`.
    //   * Function calls don't carry IDs from Gemini. We synthesize one per
    //     call and remember the function name so SendToolResults can emit
    //     functionResponse blocks with the right `name` field (which is how
    //     Gemini correlates responses back to calls).
    //   * Function-call args arrive as a structured JObject (unlike OpenAI's
    //     JSON-encoded string).
    //   * finishReason is "STOP" even when functionCalls are present — the
    //     loop decides to continue based on whether any functionCall parts
    //     appeared, not the finish reason.
    //   * Usage names differ: promptTokenCount / candidatesTokenCount /
    //     cachedContentTokenCount. Gemini's explicit context-caching is a
    //     separate API (cachedContents resource); not used here, so
    //     CacheWriteTokens stays 0.
    public class GeminiConversation : Conversation
    {
        const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models/";
        const int MaxOutputTokens = 8192;

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        readonly string _apiKey;
        readonly string _model;
        readonly ILog _log;

        JObject _systemInstruction;
        readonly JArray _contents = new JArray();
        JArray _toolsArray;

        // Tracks the function name behind each synthesized tool-call id so
        // SendToolResults can emit a functionResponse with the right `name`.
        // Cleared once results are sent — IDs only need to survive one round-trip.
        readonly Dictionary<string, string> _callNameById = new Dictionary<string, string>();

        public GeminiConversation(string apiKey, string model, ILog log)
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

            _systemInstruction = new JObject
            {
                ["parts"] = new JArray { new JObject { ["text"] = system } },
            };
            _toolsArray = SerializeTools(tools);
            _contents.Add(new JObject
            {
                ["role"] = "user",
                ["parts"] = new JArray { new JObject { ["text"] = userPrompt } },
            });

            return await PostAndCapture(ct);
        }

        public override async Task<AssistantTurn> SendToolResults(
            IReadOnlyList<ToolResult> results,
            CancellationToken ct)
        {
            JArray parts = new JArray();
            foreach (ToolResult r in results)
            {
                if (!_callNameById.TryGetValue(r.ToolUseId, out string name))
                {
                    _log.Warn($"Gemini: no function name tracked for tool id '{r.ToolUseId}'; sending as 'unknown'.");
                    name = "unknown";
                }
                JObject responseObj = new JObject();
                if (r.IsError) responseObj["error"] = r.Content;
                else responseObj["content"] = r.Content;

                parts.Add(new JObject
                {
                    ["functionResponse"] = new JObject
                    {
                        ["name"] = name,
                        ["response"] = responseObj,
                    },
                });
            }
            _callNameById.Clear();
            _contents.Add(new JObject { ["role"] = "user", ["parts"] = parts });

            return await PostAndCapture(ct);
        }

        async Task<AssistantTurn> PostAndCapture(CancellationToken ct)
        {
            JObject response = await Post(ct);
            JObject candidate = (JObject)((JArray)response["candidates"])[0];
            JObject candidateContent = (JObject)candidate["content"];
            JArray parts = (JArray)candidateContent["parts"];

            // Echo the model turn into our history so the next request has full
            // context (including any functionCall blocks Gemini emitted).
            _contents.Add(new JObject
            {
                ["role"] = "model",
                ["parts"] = parts.DeepClone(),
            });

            StringBuilder text = new StringBuilder();
            List<ToolCall> calls = new List<ToolCall>();
            int callIndex = 0;
            foreach (JToken part in parts)
            {
                if (part["text"] != null)
                {
                    text.Append((string)part["text"]);
                }
                else if (part["functionCall"] is JObject fc)
                {
                    string name = (string)fc["name"];
                    JObject args = fc["args"] as JObject ?? new JObject();
                    string id = $"gemini-call-{callIndex}-{name}";
                    callIndex++;
                    _callNameById[id] = name;
                    calls.Add(new ToolCall { Id = id, Name = name, Input = (JObject)args.DeepClone() });
                }
            }

            return BuildTurn(
                text.ToString(),
                calls,
                calls.Count > 0,
                ParseUsage(response["usageMetadata"] as JObject));
        }

        async Task<JObject> Post(CancellationToken ct)
        {
            string url = $"{EndpointBase}{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
            JObject body = new JObject
            {
                ["contents"] = _contents,
                ["systemInstruction"] = _systemInstruction,
                ["tools"] = _toolsArray,
                ["generationConfig"] = new JObject { ["maxOutputTokens"] = MaxOutputTokens },
            };

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            using (HttpResponseMessage resp = await _http.SendAsync(req, ct))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Gemini API {(int)resp.StatusCode}: {text}");
                return JObject.Parse(text);
            }
        }

        static JArray SerializeTools(IReadOnlyList<ToolSchema> tools)
        {
            JArray functionDeclarations = new JArray();
            foreach (ToolSchema t in tools)
            {
                functionDeclarations.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                });
            }
            return new JArray
            {
                new JObject { ["functionDeclarations"] = functionDeclarations },
            };
        }

        static TokenUsage ParseUsage(JObject u)
        {
            if (u == null) return default;
            int prompt = u["promptTokenCount"]?.Value<int>() ?? 0;
            int cached = u["cachedContentTokenCount"]?.Value<int>() ?? 0;
            return new TokenUsage
            {
                InputTokens = Math.Max(0, prompt - cached),
                CacheReadTokens = cached,
                CacheWriteTokens = 0, // explicit context caching is a separate Gemini API; not used here
                OutputTokens = u["candidatesTokenCount"]?.Value<int>() ?? 0,
            };
        }
    }
}
