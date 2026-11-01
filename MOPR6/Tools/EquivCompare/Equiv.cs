using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MOPR6.Core.Snapshot;

namespace MOPR6.Tools.EquivCompare
{
    /// <summary>
    /// equiv: compares the checkpoints of one scenario - vanilla runs A, B... against the mod M (design §10.4).
    ///   EquivCompare equiv --a DIR [--b DIR ...] --m DIR --scenario NAME --out DIR [--tolerances FILE] [--expected FILE]
    ///                      [--noise FILE]  (learned noise model, "noise learn")
    /// --b may be given several times (more vanilla runs = better noise estimate). Runs whose report.json records
    /// real keyboard / mouse input are refused (--allow-input: only a warning).
    /// A checkpoint is every fullsnap_&lt;label&gt;.tsv and every savecopy_&lt;label&gt; folder that exists in A or M.
    /// Writes report.md and report.json to --out; exit code 0 = no unexplained difference, 1 = some, 2 = bad input.
    /// </summary>
    static class Equiv
    {
        const int ListLimit = 50;

        sealed class Checkpoint
        {
            public string Name;            // "fullsnap:start", "save:saved"
            public int[] VanillaKeys;
            public int KeysM;
            public string[] VanillaFrames;
            public string FrameM;
            public CompareResult Result;
            public List<string> Warnings = new List<string>();
            public string Missing;          // runs that lack this checkpoint
        }

        public static int Run(string[] args)
        {
            Dictionary<string, List<string>> o = Args.Parse(args);
            string a = Get(o, "a"), m = Get(o, "m"), outDir = Get(o, "out");
            List<string> bs = o.TryGetValue("b", out List<string> list) ? list : new List<string>();
            string scenario = Get(o, "scenario") ?? "unnamed";
            if (a == null || m == null || outDir == null)
            {
                Console.Error.WriteLine("equiv --a DIR [--b DIR ...] --m DIR --scenario NAME --out DIR [--tolerances FILE] [--expected FILE]");
                return 2;
            }
            List<string> vanilla = new List<string> { a };
            vanilla.AddRange(bs);
            foreach (string d in vanilla.Concat(new[] { m }))
                if (!Directory.Exists(d)) { Console.Error.WriteLine("run folder not found: " + d); return 2; }
            bool allowInput = Get(o, "allow-input") != null;
            foreach (string d in vanilla.Concat(new[] { m }))
            {
                string input = InputOf(d);
                if (input == null) continue;
                Console.Error.WriteLine((allowInput ? "warning" : "error") + ": user input reached the game in " + d + " (" + input + ")");
                if (!allowInput) return 2;
            }

            Tolerances tol = new Tolerances(LoadList<ToleranceRule>(Get(o, "tolerances")));
            ExpectedDiffs expected = new ExpectedDiffs(LoadList<ExpectedDiff>(Get(o, "expected")));
            NoiseModel learned = Get(o, "noise") != null ? Noise.Load(Get(o, "noise")) : null;

            List<Checkpoint> points = new List<Checkpoint>();
            foreach (string label in Labels(a, m, "fullsnap_", ".tsv", false))
                points.Add(ComparePoint("fullsnap:" + label, vanilla, m, dir => ReadSnap(Path.Combine(dir, "fullsnap_" + label + ".tsv")),
                                        tol, expected, scenario, null, learned));
            foreach (string label in Labels(a, m, "savecopy_", "", true))
            {
                Checkpoint p = null;
                List<string> warnings = new List<string>();
                p = ComparePoint("save:" + label, vanilla, m, dir => ReadSave(Path.Combine(dir, "savecopy_" + label), warnings, Path.GetFileName(dir)),
                                 tol, expected, scenario, warnings, learned);
                points.Add(p);
            }

            Directory.CreateDirectory(outDir);
            WriteJson(Path.Combine(outDir, "report.json"), scenario, vanilla, m, points, expected);
            WriteMarkdown(Path.Combine(outDir, "report.md"), scenario, vanilla, m, points, expected);

            int failures = 0;
            foreach (Checkpoint p in points)
            {
                failures += (p.Result?.Failures ?? 0) + (p.Missing != null ? 1 : 0);
                string counts = p.Result == null ? "-" :
                    $"match {p.Result.Count(DiffClass.Match)}, noise {p.Result.Count(DiffClass.Noise)}" +
                    (learned != null ? $" (learned {p.Result.Of(DiffClass.Noise).Count(r => r.Reason != null && r.Reason.StartsWith("learned:", StringComparison.Ordinal))})" : "") + ", " +
                    $"explained {p.Result.Count(DiffClass.Explained)}, UNEXPLAINED {p.Result.Count(DiffClass.Unexplained)}, " +
                    $"only vanilla {p.Result.Count(DiffClass.OnlyVanilla)}, only mod {p.Result.Count(DiffClass.OnlyMod)}";
                Console.WriteLine($"{scenario} {p.Name,-26} {counts}{(p.Missing != null ? "  MISSING in " + p.Missing : "")}");
            }
            if (points.Count == 0) { Console.WriteLine(scenario + ": no checkpoints found"); return 2; }
            Console.WriteLine($"vanilla runs: {vanilla.Count}. " + (failures == 0 ? "OK: no unexplained differences" : $"FAIL: {failures} unexplained difference(s)"));
            return failures == 0 ? 0 : 1;
        }

        // ----- Checkpoints --------------------------------------------------------------------------------

        internal static IEnumerable<string> Labels(string a, string m, string prefix, string suffix, bool folders)
        {
            SortedSet<string> labels = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string dir in new[] { a, m })
            {
                IEnumerable<string> items = folders ? Directory.GetDirectories(dir, prefix + "*") : Directory.GetFiles(dir, prefix + "*" + suffix);
                foreach (string p in items)
                {
                    string n = Path.GetFileName(p);
                    labels.Add(n.Substring(prefix.Length, n.Length - prefix.Length - suffix.Length));
                }
            }
            return labels;
        }

        static Checkpoint ComparePoint(string name, List<string> vanilla, string m, Func<string, SnapshotData> read,
                                       Tolerances tol, ExpectedDiffs exp, string scenario, List<string> warnings, NoiseModel learned)
        {
            Checkpoint p = new Checkpoint { Name = name };
            if (warnings != null) p.Warnings = warnings;
            List<SnapshotData> vs = vanilla.Select(read).ToList();
            SnapshotData sm = read(m);
            List<string> missing = new List<string>();
            for (int i = 0; i < vs.Count; i++) if (vs[i] == null) missing.Add(i == 0 ? "A" : "B" + i);
            if (sm == null) missing.Add("M");
            if (missing.Count > 0)
            {
                p.Missing = string.Join(", ", missing);
                return p;
            }
            p.VanillaKeys = vs.Select(v => v.Count).ToArray();
            p.KeysM = sm.Count;
            p.VanillaFrames = vs.Select(v => v.Meta.TryGetValue("frame", out string f) ? f : "-").ToArray();
            p.FrameM = sm.Meta.TryGetValue("frame", out string fm) ? fm : "-";
            p.Result = SnapshotComparer.Compare(vs, sm, tol, exp, scenario, learned);
            return p;
        }

        /// <summary>The TestBot's note about real keyboard / mouse input in this run (report.json), or null.</summary>
        static string InputOf(string runDir)
        {
            string file = Path.Combine(runDir, "report.json");
            if (!File.Exists(file)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("InputFrames", out JsonElement n) || n.ValueKind != JsonValueKind.Number || n.GetInt32() == 0)
                return null;
            // With the TestBot's input lock the input never reached the game's readers.
            if (doc.RootElement.TryGetProperty("InputLocked", out JsonElement locked) && locked.ValueKind == JsonValueKind.True)
                return null;
            string first = doc.RootElement.TryGetProperty("InputFirst", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "?";
            return n.GetInt32() + " frame(s), first: " + first;
        }

        internal static SnapshotData ReadSnap(string file)
        {
            if (!File.Exists(file)) return null;
            using StreamReader r = new StreamReader(file, new UTF8Encoding(false));
            return SnapshotData.Parse(r);
        }

        internal static SnapshotData ReadSave(string dir, List<string> warnings, string run)
        {
            if (!Directory.Exists(dir)) return null;
            List<string> w = new List<string>();
            SnapshotData d = SaveSnapshot.FromFolder(dir, w);
            foreach (string s in w) warnings.Add(run + ": " + s);
            return d;
        }

        // ----- Input files --------------------------------------------------------------------------------

        static readonly JsonSerializerOptions JsonIn = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        internal static List<T> LoadList<T>(string file)
        {
            if (file == null) return new List<T>();
            if (!File.Exists(file)) throw new ArgumentException("file not found: " + file);
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), JsonIn) ?? new List<T>();
        }

        static string Get(Dictionary<string, List<string>> o, string key) => o.TryGetValue(key, out List<string> v) ? v[v.Count - 1] : null;

        // ----- Reports ------------------------------------------------------------------------------------

        static void WriteJson(string file, string scenario, List<string> vanilla, string m, List<Checkpoint> points, ExpectedDiffs exp)
        {
            var doc = new
            {
                Scenario = scenario,
                Vanilla = vanilla,
                Mod = m,
                Failures = points.Sum(p => (p.Result?.Failures ?? 0) + (p.Missing != null ? 1 : 0)),
                Checkpoints = points.Select(p => new
                {
                    p.Name,
                    p.Missing,
                    p.VanillaKeys,
                    p.KeysM,
                    p.VanillaFrames,
                    p.FrameM,
                    Counts = p.Result == null ? null : Enum.GetValues(typeof(DiffClass)).Cast<DiffClass>().ToDictionary(c => c.ToString(), c => p.Result.Count(c)),
                    p.Warnings,
                    Differences = p.Result?.Records.Where(r => r.Class != DiffClass.Noise).Select(r => new { r.Key, Class = r.Class.ToString(), r.A, r.B, r.M, r.Reason }),
                    Noise = p.Result?.Records.Where(r => r.Class == DiffClass.Noise).Select(r => new { r.Key, r.A, r.B, r.M, r.Reason })
                }),
                StaleExpected = exp.Stale().Select(e => new { e.Pattern, e.Scenario, e.Reason })
            };
            File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        static void WriteMarkdown(string file, string scenario, List<string> vanilla, string m, List<Checkpoint> points, ExpectedDiffs exp)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Equivalence: ").Append(scenario).Append("\n\n");
            for (int i = 0; i < vanilla.Count; i++)
                sb.Append("- ").Append(i == 0 ? "A" : "B" + i).Append(" (vanilla): `").Append(vanilla[i]).Append("`\n");
            sb.Append("- M (mod): `").Append(m).Append("`\n\n");
            sb.Append("| Checkpoint | Frames (vanilla / M) | Keys M | Match | Noise | Explained | Unexplained | Only vanilla | Only mod |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|\n");
            foreach (Checkpoint p in points)
            {
                if (p.Result == null)
                {
                    sb.Append("| ").Append(p.Name).Append(" | missing in ").Append(p.Missing).Append(" | | | | | | | |\n");
                    continue;
                }
                CompareResult r = p.Result;
                sb.Append("| ").Append(p.Name)
                  .Append(" | ").Append(string.Join("/", p.VanillaFrames)).Append(" / ").Append(p.FrameM)
                  .Append(" | ").Append(N(p.KeysM))
                  .Append(" | ").Append(N(r.Count(DiffClass.Match))).Append(" | ").Append(N(r.Count(DiffClass.Noise)))
                  .Append(" | ").Append(N(r.Count(DiffClass.Explained))).Append(" | **").Append(N(r.Count(DiffClass.Unexplained)))
                  .Append("** | ").Append(N(r.Count(DiffClass.OnlyVanilla))).Append(" | ").Append(N(r.Count(DiffClass.OnlyMod))).Append(" |\n");
            }

            foreach (Checkpoint p in points)
            {
                if (p.Result == null) continue;
                List<DiffRecord> bad = p.Result.Records.Where(r => r.Class == DiffClass.Unexplained || r.Class == DiffClass.OnlyVanilla || r.Class == DiffClass.OnlyMod).ToList();
                List<DiffRecord> noise = p.Result.Records.Where(r => r.Class == DiffClass.Noise).ToList();
                if (bad.Count == 0 && noise.Count == 0 && p.Warnings.Count == 0) continue;
                sb.Append("\n## ").Append(p.Name).Append("\n\n");
                foreach (string w in p.Warnings.Distinct()) sb.Append("- warning: ").Append(w).Append('\n');
                if (bad.Count > 0)
                {
                    sb.Append("\nUnexplained (first ").Append(Math.Min(ListLimit, bad.Count)).Append(" of ").Append(bad.Count).Append("):\n\n");
                    sb.Append("| Class | Key | A | B | M |\n|---|---|---|---|---|\n");
                    foreach (DiffRecord r in bad.Take(ListLimit))
                        sb.Append("| ").Append(r.Class).Append(" | `").Append(r.Key).Append("` | ").Append(Cell(r.A)).Append(" | ")
                          .Append(Cell(r.B)).Append(" | ").Append(Cell(r.M)).Append(" |\n");
                }
                if (noise.Count > 0)
                {
                    sb.Append("\nNoise by key group (").Append(noise.Count).Append(" keys):\n\n");
                    foreach (KeyValuePair<string, int> g in Groups(noise).Take(ListLimit))
                        sb.Append("- `").Append(g.Key).Append("`: ").Append(g.Value).Append('\n');
                }
            }

            List<ExpectedDiff> stale = exp.Stale();
            if (stale.Count > 0)
            {
                sb.Append("\n## Expected differences that matched nothing\n\n");
                foreach (ExpectedDiff e in stale) sb.Append("- `").Append(e.Pattern).Append("` (").Append(e.Scenario ?? "*").Append("): ").Append(e.Reason).Append('\n');
            }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Keys grouped by root object and key kind ("TRAFFIC/….pose"), most frequent first.</summary>
        public static IEnumerable<KeyValuePair<string, int>> Groups(IEnumerable<DiffRecord> records)
        {
            Dictionary<string, int> g = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DiffRecord r in records)
            {
                string k = r.Key;
                int slash = k.IndexOfAny(new[] { '/', '#', '|' });
                int dot = k.LastIndexOf('.');
                string root = slash > 0 ? k.Substring(0, slash) : (dot > 0 ? k.Substring(0, dot) : k);
                string kind = dot > 0 ? k.Substring(dot) : "";
                int bang = k.LastIndexOf('!');
                if (bang > 0) kind = k.Substring(bang);
                if (k.IndexOf('#') > 0) kind = "#…" + kind;
                string key = root + "/…" + kind;
                g[key] = (g.TryGetValue(key, out int n) ? n : 0) + 1;
            }
            return g.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);
        }

        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        static string Cell(string v)
        {
            if (v == null) return "—";
            if (v.Length > 80) v = v.Substring(0, 77) + "...";
            return "`" + v.Replace("|", "\\|").Replace("`", "'") + "`";
        }
    }
}
