using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace CityStoryMod.Storyteller
{
    // Resolves the "current instance" file pointers the storyteller would
    // otherwise have to discover by listing directories (GH #45).
    //
    // The agent runs under `claude -p`, where every tool-result cycle is a full
    // model round-trip. It already knows the *shape* of the city folder cold —
    // CLAUDE.md carries the whole tree — so what actually costs latency is
    // resolving the two files whose names aren't fixed: the latest
    // `snapshots/snapshot-<ts>.json` and the open `sessions/SXX-…-open.md`.
    // Both are cheap for the mod to answer (it writes them), so it publishes
    // them in clock.json and the two listing round-trips disappear.
    //
    // Deliberately pure .NET + Newtonsoft — no Unity, no Game.dll — so the
    // net48 test project can link it (see CLAUDE.md → Testing). Every resolver
    // is best-effort: anything unreadable comes back null/false rather than
    // throwing into the clock heartbeat.
    public static class CityPointers
    {
        // Relative path (forward slashes, city-dir-relative — the agent's cwd)
        // of the newest snapshot, or null when there are none. Recency comes
        // from the unix timestamp baked into the filename, which is the same
        // ordering the retention sweep uses; a name that doesn't parse falls
        // back to its last-write time so a hand-copied file still sorts.
        public static string ResolveLatestSnapshot(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return null;
            string dir = Path.Combine(cityDir, "snapshots");
            string[] files;
            try { files = Directory.GetFiles(dir, "snapshot-*.json"); }
            catch (Exception) { return null; }

            string best = null;
            long bestKey = long.MinValue;
            foreach (string path in files)
            {
                long key;
                try
                {
                    string name = Path.GetFileNameWithoutExtension(path);   // snapshot-<ts>
                    int dash = name.IndexOf('-');
                    key = (dash >= 0 && long.TryParse(name.Substring(dash + 1), out long ts))
                        ? ts
                        : new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                }
                catch (Exception) { continue; }

                if (key > bestKey)
                {
                    bestKey = key;
                    best = Path.GetFileName(path);
                }
            }

            return best == null ? null : "snapshots/" + best;
        }

        // Relative path of the open session file, or null when the most recent
        // session is already closed (or there are none).
        //
        // Mirrors the agent's own open-session rule exactly: look at the *most
        // recent* session only (highest SXX), and treat a missing
        // `ended_real_date:` in its frontmatter as open. Reading just that one
        // candidate keeps the clock heartbeat to a single small file read
        // regardless of how long the playthrough has run.
        public static string ResolveOpenSession(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return null;
            string dir = Path.Combine(cityDir, "sessions");
            string[] files;
            try { files = Directory.GetFiles(dir, "S*-*.md"); }
            catch (Exception) { return null; }

            string best = null;
            int bestN = int.MinValue;
            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                int dash = name.IndexOf('-');
                if (dash <= 1) continue;
                if (!int.TryParse(name.Substring(1, dash - 1), out int n)) continue;
                // Ties (two files claiming the same SXX) resolve by name so the
                // answer is stable across ticks rather than filesystem order.
                if (n > bestN || (n == bestN && string.CompareOrdinal(name, best) > 0))
                {
                    bestN = n;
                    best = name;
                }
            }

            if (best == null) return null;
            try
            {
                string content = File.ReadAllText(Path.Combine(dir, best));
                if (TextUtils.FrontmatterHasEndedRealDate(content)) return null;
            }
            catch (Exception) { return null; }

            return "sessions/" + best;
        }

        // settings.json's `bootstrapped` flag — true once /new-city has run.
        // Published so an opener doesn't have to read settings.json just to
        // know whether the city has been founded. Missing file / missing flag /
        // unparseable JSON all read as false, matching the panel's own check.
        public static bool ResolveBootstrapped(string cityDir)
        {
            if (string.IsNullOrEmpty(cityDir)) return false;
            try
            {
                string path = Path.Combine(cityDir, "settings.json");
                if (!File.Exists(path)) return false;
                var settings = JObject.Parse(File.ReadAllText(path));
                return settings["bootstrapped"]?.Value<bool?>() ?? false;
            }
            catch (Exception) { return false; }
        }
    }
}
