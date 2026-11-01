using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MOPR6.Core.Bench;

namespace MOPR6.Tools.EquivCompare
{
    /// <summary>
    /// bench: the benchmark table and the release gate (requirement 4, design §11).
    ///   EquivCompare bench --vanilla DIR [...] --ref DIR [...] --cand DIR [...] --out DIR
    /// Every DIR is a test-run folder with report.json (TestBot "measure" steps). --ref = MOPR 5.1.1, --cand = MOPR6.
    /// Writes bench.md and bench.json; exit code 0 = the gate passes (every point, average FPS and p99), 1 = it fails,
    /// 2 = bad input.
    /// </summary>
    static class Bench
    {
        sealed class Point
        {
            public string Label;
            public readonly Dictionary<string, List<Measure>> ByVariant = new Dictionary<string, List<Measure>>();
        }

        sealed class Measure
        {
            public double AvgFps, P50, P95, P99, AllocMBs, Gc;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] Variants = { "vanilla", "ref", "cand" };

        public static int Run(string[] args)
        {
            Dictionary<string, List<string>> o = Args.Parse(args);
            if (!o.ContainsKey("ref") || !o.ContainsKey("cand") || !o.ContainsKey("out"))
            {
                Console.Error.WriteLine("bench --vanilla DIR [...] --ref DIR [...] --cand DIR [...] --out DIR");
                return 2;
            }

            Dictionary<string, Point> points = new Dictionary<string, Point>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            List<string> problems = new List<string>();
            foreach (string variant in Variants)
            {
                if (!o.TryGetValue(variant, out List<string> dirs)) continue;
                foreach (string dir in dirs)
                {
                    string file = Path.Combine(dir, "report.json");
                    if (!File.Exists(file)) { problems.Add("no report.json in " + dir); continue; }
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("Error", out JsonElement err) && err.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(err.GetString()))
                        problems.Add(dir + ": run error: " + err.GetString());
                    if (!root.TryGetProperty("Measures", out JsonElement measures) || measures.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement m in measures.EnumerateArray())
                    {
                        string label = m.GetProperty("Label").GetString() ?? "?";
                        if (!points.TryGetValue(label, out Point p)) { points[label] = p = new Point { Label = label }; order.Add(label); }
                        if (!p.ByVariant.TryGetValue(variant, out List<Measure> list)) p.ByVariant[variant] = list = new List<Measure>();
                        list.Add(new Measure
                        {
                            AvgFps = m.GetProperty("AvgFps").GetDouble(),
                            P50 = m.GetProperty("FrameMsP50").GetDouble(),
                            P95 = m.GetProperty("FrameMsP95").GetDouble(),
                            P99 = m.GetProperty("FrameMsP99").GetDouble(),
                            AllocMBs = m.GetProperty("AllocMBPerSec").GetDouble(),
                            Gc = m.GetProperty("GcCollections").GetDouble()
                        });
                    }
                }
            }
            if (points.Count == 0) { Console.Error.WriteLine("no measurements found"); return 2; }

            List<GateCheck> checks = new List<GateCheck>();
            foreach (string label in order)
            {
                Point p = points[label];
                checks.Add(BenchGate.Check(label, GateMetric.AvgFps, Sample(p, "cand", x => x.AvgFps), Sample(p, "ref", x => x.AvgFps)));
                checks.Add(BenchGate.Check(label, GateMetric.P99Ms, Sample(p, "cand", x => x.P99), Sample(p, "ref", x => x.P99)));
            }
            bool pass = BenchGate.AllPass(checks);

            Directory.CreateDirectory(o["out"][0]);
            WriteMarkdown(Path.Combine(o["out"][0], "bench.md"), order, points, checks, pass, problems, o);
            File.WriteAllText(Path.Combine(o["out"][0], "bench.json"), JsonSerializer.Serialize(new
            {
                Pass = pass,
                Problems = problems,
                Points = order.Select(l => new
                {
                    Label = l,
                    Variants = points[l].ByVariant.ToDictionary(kv => kv.Key, kv => kv.Value)
                }),
                Gate = checks.Select(c => new { c.Point, Metric = c.Metric.ToString(), c.Candidate, c.Reference, c.Sigma, c.Margin, c.DeltaPercent, c.Pass, c.Missing })
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }), new UTF8Encoding(false));

            foreach (string pr in problems) Console.WriteLine("warning: " + pr);
            foreach (string label in order)
            {
                Point p = points[label];
                Console.WriteLine($"{label,-12} vanilla {Cell(p, "vanilla", x => x.AvgFps, "0.0"),-14} 5.1.1 {Cell(p, "ref", x => x.AvgFps, "0.0"),-14} MOPR6 {Cell(p, "cand", x => x.AvgFps, "0.0"),-14} FPS");
            }
            foreach (GateCheck c in checks) Console.WriteLine("  " + c);
            Console.WriteLine(pass ? "GATE: pass" : "GATE: FAIL");
            return pass ? 0 : 1;
        }

        static Sample Sample(Point p, string variant, Func<Measure, double> f) =>
            p.ByVariant.TryGetValue(variant, out List<Measure> l) ? new Sample(l.Select(f)) : new Sample();

        static string Cell(Point p, string variant, Func<Measure, double> f, string format)
        {
            Sample s = Sample(p, variant, f);
            if (s.Count == 0) return "—";
            string mean = s.Mean.ToString(format, Inv);
            return s.Count > 1 ? mean + " ±" + s.StdDev.ToString(format, Inv) + " (" + s.Count + ")" : mean;
        }

        static void WriteMarkdown(string file, List<string> order, Dictionary<string, Point> points, List<GateCheck> checks, bool pass,
                                  List<string> problems, Dictionary<string, List<string>> o)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Benchmarks\n\n");
            foreach (string v in Variants)
                if (o.TryGetValue(v, out List<string> dirs))
                    foreach (string d in dirs) sb.Append("- ").Append(Name(v)).Append(": `").Append(d).Append("`\n");
            foreach (string pr in problems) sb.Append("- warning: ").Append(pr).Append('\n');

            (string title, Func<Measure, double> f, string fmt)[] metrics =
            {
                ("Average FPS", x => x.AvgFps, "0.0"),
                ("Median frame, ms", x => x.P50, "0.00"),
                ("p95 frame, ms", x => x.P95, "0.00"),
                ("p99 frame, ms", x => x.P99, "0.00"),
                ("Allocation, MB/s", x => x.AllocMBs, "0.00"),
                ("Garbage collections", x => x.Gc, "0.0")
            };
            foreach (var m in metrics)
            {
                sb.Append("\n## ").Append(m.title).Append("\n\n| Point | Vanilla | MOPR 5.1.1 | MOPR6 |\n|---|---|---|---|\n");
                foreach (string label in order)
                {
                    Point p = points[label];
                    sb.Append("| ").Append(label).Append(" | ").Append(Cell(p, "vanilla", m.f, m.fmt)).Append(" | ")
                      .Append(Cell(p, "ref", m.f, m.fmt)).Append(" | ").Append(Cell(p, "cand", m.f, m.fmt)).Append(" |\n");
                }
            }

            sb.Append("\n## Gate: MOPR6 not slower than MOPR 5.1.1 (").Append(pass ? "pass" : "**FAIL**").Append(")\n\n");
            sb.Append("| Point | Metric | MOPR6 | 5.1.1 | Δ | Margin | Result |\n|---|---|---|---|---|---|---|\n");
            foreach (GateCheck c in checks)
            {
                if (c.Missing) { sb.Append("| ").Append(c.Point).Append(" | ").Append(c.Metric).Append(" | — | — | — | — | missing |\n"); continue; }
                sb.Append("| ").Append(c.Point).Append(" | ").Append(c.Metric).Append(" | ").Append(c.Candidate.ToString("0.00", Inv))
                  .Append(" | ").Append(c.Reference.ToString("0.00", Inv)).Append(" | ").Append(c.DeltaPercent.ToString("+0.0;-0.0", Inv)).Append("% | ")
                  .Append(c.Margin.ToString("0.00", Inv)).Append(" | ").Append(c.Pass ? "pass" : "**FAIL**").Append(" |\n");
            }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }

        static string Name(string variant) => variant == "ref" ? "MOPR 5.1.1" : variant == "cand" ? "MOPR6" : "vanilla";
    }

    /// <summary>"--key value" pairs; a key may repeat ("--b DIR --b DIR").</summary>
    static class Args
    {
        public static Dictionary<string, List<string>> Parse(string[] args)
        {
            Dictionary<string, List<string>> o = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unexpected argument: " + args[i]);
                string key = args[i].Substring(2);
                string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                if (!o.TryGetValue(key, out List<string> l)) o[key] = l = new List<string>();
                l.Add(value);
            }
            return o;
        }
    }
}
