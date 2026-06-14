using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // The four file/dir tools the storyteller exposes to every provider. Each
    // provider's Conversation subclass serializes these into its native tool
    // format (Anthropic's input_schema, OpenAI's function.parameters, Gemini's
    // FunctionDeclaration, Ollama's OpenAI-compatible shape).
    public static class ToolSchemas
    {
        public static IReadOnlyList<ToolSchema> Default => _defaults;

        static readonly IReadOnlyList<ToolSchema> _defaults = new[]
        {
            new ToolSchema
            {
                Name = "read_file",
                Description = "Read a UTF-8 text file inside the city dir. Returns the file contents.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir, starting with a subdirectory name (e.g. 'canon/city.md', 'sessions/2026-01-12-session-start.md'). No leading slash." },
                    },
                    ["required"] = new JArray { "path" },
                },
            },
            new ToolSchema
            {
                Name = "write_file",
                Description = "Write a UTF-8 text file inside the city dir. Overwrites existing files. Creates parent directories as needed.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir, starting with a subdirectory name (e.g. 'canon/city.md', 'sessions/2026-01-12-session-start.md'). No leading slash." },
                        ["content"] = new JObject { ["type"] = "string", ["description"] = "UTF-8 text to write." },
                    },
                    ["required"] = new JArray { "path", "content" },
                },
            },
            new ToolSchema
            {
                Name = "list_dir",
                Description = "List entries in a directory inside the city dir. Returns one entry per line, with 'dir:' or 'file:' prefix.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["path"] = new JObject { ["type"] = "string", ["description"] = "Path relative to the city dir, e.g. 'canon' or 'sessions'. Use '.' for the city root. No leading slash." },
                    },
                    ["required"] = new JArray { "path" },
                },
            },
            new ToolSchema
            {
                Name = "glob",
                Description = "Find files matching a wildcard pattern (e.g. 'canon/*.md', 'sessions/**/*.md') inside the city dir. Returns matching paths one per line.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["pattern"] = new JObject { ["type"] = "string", ["description"] = "Wildcard pattern relative to the city dir." },
                    },
                    ["required"] = new JArray { "pattern" },
                },
            },
            // Quickstart founding callback. Called once at the end of a
            // <<QUICKSTART_CONFIG>>-driven /new-city run so the native wizard's
            // result card can show the founding without re-parsing canon. The
            // payload is also derivable from canon/city.md, and settings.json's
            // bootstrapped:true is the authoritative completion signal — this
            // tool is the fast path, not the source of truth.
            new ToolSchema
            {
                Name = "wizard_done",
                Description = "Call once at the end of a quickstart founding to report the result to the native UI. Only call this when a <<QUICKSTART_CONFIG>> block was present in the prompt.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject {
                        ["city_name"] = new JObject { ["type"] = "string", ["description"] = "The founded city's name." },
                        ["region"] = new JObject { ["type"] = "string", ["description"] = "The pinned region (one of the canon region enum values)." },
                        ["founded"] = new JObject { ["type"] = "string", ["description"] = "Founding year or era phrase, if one was written." },
                        ["premise"] = new JObject { ["type"] = "string", ["description"] = "One-sentence playthrough premise." },
                    },
                    ["required"] = new JArray { "city_name", "region", "premise" },
                },
            },
        };
    }
}
