using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MOPR6.Core.Snapshot;

namespace MOPR6.Tools.EquivCompare
{
    /// <summary>
    /// noise learn --runs runs.json [--runs ...] --out MOPR6\Equiv\noise.json [--tolerances FILE] [--fresh]
    ///   Every key on which the vanilla runs of a scenario disagree, at any checkpoint, joins the learned noise model
    ///   (NoiseModel). runs.json is the list equiv.ps1 writes (Scenario, Kind, Dir, Code). The existing --out file is
    ///   merged unless --fresh.
    /// </summary>
    static class Noise
    {
        sealed class RunEntry
        {
            public string Scenario { get; set; }
            public string Kind { get; set; }
            public string Dir { get; set; }
            public string Code { get; set; }
        }

        public static int Run(string[] args)
        {
            if (args.Length == 0 || !args[0].Equals("learn", StringComparison.OrdinalIgnoreCase)) return Usage();
            Dictionary<string, List<string>> o = Args.Parse(args.Skip(1).ToArray());
            if (!o.TryGetValue("runs", out List<string> runFiles) || !o.TryGetValue("out", out List<string> outs)) return Usage();
            string output = outs[outs.Count - 1];
            Tolerances tol = new Tolerances(Equiv.LoadList<ToleranceRule>(o.TryGetValue("tolerances", out List<string> t) ? t[t.Count - 1] : null));

            NoiseModel model = new NoiseModel();
            if (!o.ContainsKey("fresh") && File.Exists(output))
                foreach (NoiseEntry e in Load(output).Entries) model.Add(e);
            int before = model.Count;

            List<RunEntry> runs = new List<RunEntry>();
            foreach (string f in runFiles)
            {
                string text = File.ReadAllText(f).TrimStart('\uFEFF');
                // ConvertTo-Json of a single run writes an object, of several an array.
                if (text.TrimStart().StartsWith("{", StringComparison.Ordinal)) text = "[" + text + "]";
                runs.AddRange(JsonSerializer.Deserialize<List<RunEntry>>(text) ?? new List<RunEntry>());
            }
            foreach (IGrouping<string, RunEntry> g in runs.Where(r => r.Kind == "vanilla" && r.Code == "0" && Directory.Exists(r.Dir)).GroupBy(r => r.Scenario))
            {
                List<string> dirs = g.Select(r => r.Dir).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (dirs.Count < 2) { Console.WriteLine($"{g.Key}: {dirs.Count} vanilla run - skipped"); continue; }
                foreach (string label in Equiv.Labels(dirs[0], dirs[0], "fullsnap_", ".tsv", false))
                {
                    List<SnapshotData> vs = dirs.Select(d => Equiv.ReadSnap(Path.Combine(d, "fullsnap_" + label + ".tsv"))).Where(s => s != null).ToList();
                    int n = model.Learn(vs, tol, g.Key + "/fullsnap:" + label);
                    Console.WriteLine($"{g.Key} fullsnap:{label,-14} {vs.Count} runs, {n} keys varied");
                }
                foreach (string label in Equiv.Labels(dirs[0], dirs[0], "savecopy_", "", true))
                {
                    List<string> warnings = new List<string>();
                    List<SnapshotData> vs = dirs.Select(d => Equiv.ReadSave(Path.Combine(d, "savecopy_" + label), warnings, Path.GetFileName(d))).Where(s => s != null).ToList();
                    int n = model.Learn(vs, tol, g.Key + "/save:" + label);
                    Console.WriteLine($"{g.Key} save:{label,-18} {vs.Count} runs, {n} keys varied");
                }
            }
            Save(model, output);
            Console.WriteLine($"noise model: {model.Count} keys ({model.Count - before} new) -> {output}");
            return 0;
        }

        static int Usage()
        {
            Console.Error.WriteLine("EquivCompare noise learn --runs runs.json [--runs ...] --out noise.json [--tolerances FILE] [--fresh]");
            return 2;
        }

        public static NoiseModel Load(string file)
        {
            NoiseModel m = new NoiseModel();
            if (file == null) return m;
            if (!File.Exists(file)) throw new ArgumentException("file not found: " + file);
            foreach (NoiseEntry e in Equiv.LoadList<NoiseEntry>(file)) m.Add(e);
            return m;
        }

        static void Save(NoiseModel model, string file)
        {
            // One entry per line, sorted: a diff of two calibrations stays readable.
            StringBuilder sb = new StringBuilder("[\n");
            List<NoiseEntry> list = model.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append("  ").Append(JsonSerializer.Serialize(new { list[i].Key, list[i].Seen }));
                sb.Append(i + 1 < list.Count ? ",\n" : "\n");
            }
            sb.Append("]\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
