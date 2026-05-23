using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityStoryMod
{
    // Keeps each city's scaffolded template files in sync with the embedded
    // template/ tree that ships in the mod DLL. Replaces the old one-shot
    // "if CLAUDE.md exists, skip" scaffolder — every export runs Sync, and
    // individual files migrate forward only when we can prove the player
    // hasn't edited them.
    //
    // State model: `.template-manifest.json` at the city dir root records
    // the content hash of every template file *the mod has written or
    // updated*. A file is "tracked" if it's in the manifest; "untracked"
    // otherwise. Tracking is what lets us tell "mod wrote v1, player didn't
    // touch, safe to update to v2" apart from "found this file already on
    // disk when we first ran, don't know what the player did to it."
    //
    // Per-file decision tree (when embedded ships path P with hash E):
    //
    //   P missing on disk                       → write file, track with E
    //   P on disk, P tracked, diskHash == E     → no-op, manifest stays E
    //   P on disk, P tracked, diskHash == M     → mod wrote it last;
    //                                             update to E, track with E
    //   P on disk, P tracked, diskHash != M, E  → player edited what we
    //                                             wrote — DIVERGENT,
    //                                             leave alone, keep M
    //   P on disk, P untracked, diskHash == E   → legacy file matches
    //                                             current template, adopt
    //                                             into manifest (free win)
    //   P on disk, P untracked, diskHash != E   → DIVERGENT (legacy or
    //                                             player-authored), leave
    //                                             alone, don't track
    //
    // Bootstrap: legacy cities (scaffolded before this mechanism shipped,
    // no manifest yet) start with everything untracked. We never auto-
    // update an untracked file — we don't know what the player has done
    // with it. If embedded hash happens to match disk, we silently adopt
    // (now tracked); from then on it follows the standard rules.
    //
    // Pure C# — no Unity / Game.dll / Colossal.* references — so the test
    // project can link this file directly and exercise the logic against
    // real temp directories.
    public static class TemplateScaffolder
    {
        public struct TemplateFile
        {
            // Path relative to city dir, forward slashes. e.g. "CLAUDE.md"
            // or ".claude/commands/session-start.md".
            public string RelativePath;
            public byte[] Content;
        }

        // Sync result reported back to the caller (mainly for logging).
        public struct SyncResult
        {
            public int Added;       // files written for the first time
            public int Updated;     // files brought forward from manifest hash
            public int Unchanged;   // files already in sync, no I/O performed
            public List<string> Divergent; // relative paths the player edited; left alone
        }

        const string ManifestFileName = ".template-manifest.json";

        // Top-level entry. Walks `embedded`, writes/updates as needed, and
        // produces a SyncResult. Caller is expected to log the result.
        public static SyncResult Sync(string cityDir, IEnumerable<TemplateFile> embedded)
        {
            if (string.IsNullOrEmpty(cityDir))
                throw new ArgumentException("cityDir is required", nameof(cityDir));
            Directory.CreateDirectory(cityDir);

            string manifestPath = Path.Combine(cityDir, ManifestFileName);
            Dictionary<string, string> manifest = ReadManifest(manifestPath);
            Dictionary<string, string> nextManifest = new Dictionary<string, string>();
            var result = new SyncResult { Divergent = new List<string>() };

            foreach (TemplateFile file in embedded)
            {
                string rel = file.RelativePath;
                string diskPath = ResolveDiskPath(cityDir, rel);
                string embeddedHash = HashBytes(file.Content);
                string manifestHash;
                bool tracked = manifest.TryGetValue(rel, out manifestHash);

                if (!File.Exists(diskPath))
                {
                    // Missing on disk → write + track. Covers brand-new
                    // template files and player-deleted ones; both want
                    // the canonical template content restored.
                    WriteFile(diskPath, file.Content);
                    nextManifest[rel] = embeddedHash;
                    result.Added++;
                    continue;
                }

                string diskHash = HashFile(diskPath);

                if (diskHash == embeddedHash)
                {
                    // On disk and matches the current embedded version. No
                    // I/O needed. Adopt into manifest if we weren't already
                    // tracking — free win for legacy cities whose files
                    // happen to be current.
                    nextManifest[rel] = embeddedHash;
                    result.Unchanged++;
                    continue;
                }

                if (tracked && diskHash == manifestHash)
                {
                    // Tracked and disk matches what the mod last wrote;
                    // template has evolved since. Safe to update — the
                    // player demonstrably hasn't touched the file.
                    WriteFile(diskPath, file.Content);
                    nextManifest[rel] = embeddedHash;
                    result.Updated++;
                    continue;
                }

                // Two divergent flavors collapse here:
                //   - Tracked + diskHash != manifestHash: player edited
                //     what the mod wrote.
                //   - Untracked + diskHash != embeddedHash: legacy/player
                //     file the mod never managed.
                // Either way, leave on-disk alone. Tracked entries preserve
                // their manifest hash so we never silently overwrite the
                // player's edits later. Untracked entries stay untracked —
                // we still don't claim responsibility.
                if (tracked) nextManifest[rel] = manifestHash;
                result.Divergent.Add(rel);
            }

            WriteManifest(manifestPath, nextManifest);
            return result;
        }

        // Combines cityDir with rel using the host's path separator. rel is
        // a forward-slash POSIX-style relative path; both separators work
        // on Windows but Path.Combine wants the host-native one.
        static string ResolveDiskPath(string cityDir, string rel)
        {
            string normalized = rel.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(cityDir, normalized);
        }

        static void WriteFile(string path, byte[] content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, content);
        }

        static Dictionary<string, string> ReadManifest(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return dict;
            try
            {
                string text = File.ReadAllText(path);
                JObject obj = JObject.Parse(text);
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value.Type == JTokenType.String)
                        dict[prop.Name] = (string)prop.Value;
                }
            }
            catch
            {
                // Malformed manifest is treated as "no prior state". The
                // sync will bootstrap from disk as if the file didn't exist.
            }
            return dict;
        }

        static void WriteManifest(string path, Dictionary<string, string> manifest)
        {
            var obj = new JObject();
            foreach (var kv in manifest) obj[kv.Key] = kv.Value;
            string serialized = obj.ToString(Formatting.Indented);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, serialized);
        }

        static string HashBytes(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data);
                return BytesToHex(digest);
            }
        }

        static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] digest = sha.ComputeHash(stream);
                return BytesToHex(digest);
            }
        }

        static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
