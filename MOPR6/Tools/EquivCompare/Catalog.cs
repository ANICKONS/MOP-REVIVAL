using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MOPR6.Core.Catalog;

namespace MOPR6.Tools.EquivCompare
{
    /// <summary>
    /// "catalog build --in inventory_x.jsonl --out MOPR6\Resources\catalog\inventory.json": the TestBot inventory
    /// dump of vanilla -> the embedded inventory (design §5). Refuses to write when the dump has problems.
    /// "catalog diff --a OLD.json --b NEW.json": what changed between two inventories (a game update, a second dump).
    /// </summary>
    static class Catalog
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0) return Usage();
            Dictionary<string, string> o = Options(args, 1);
            switch (args[0].ToLowerInvariant())
            {
                case "build": return Build(o);
                case "diff": return Diff(o);
                default: return Usage();
            }
        }

        static int Usage()
        {
            Console.Error.WriteLine("EquivCompare catalog build --in DUMP.jsonl --out inventory.json");
            Console.Error.WriteLine("EquivCompare catalog diff --a OLD.json --b NEW.json");
            return 2;
        }

        static int Build(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("in", out string input) || !o.TryGetValue("out", out string output)) return Usage();
            List<string> problems = new List<string>();
            Inventory inv;
            using (StreamReader r = new StreamReader(input, Encoding.UTF8)) inv = InventoryIO.FromDump(r, problems);
            inv.Created = File.GetLastWriteTimeUtc(input).ToString("yyyy-MM-dd");
            Console.WriteLine("inventory: " + InventoryIO.Summary(inv));
            if (problems.Count > 0)
            {
                foreach (string p in problems.GetRange(0, Math.Min(30, problems.Count))) Console.Error.WriteLine("  " + p);
                Console.Error.WriteLine($"{problems.Count} problems: not written");
                return 1;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            using (StreamWriter w = new StreamWriter(output, false, new UTF8Encoding(false))) InventoryIO.Write(inv, w);
            Console.WriteLine($"written {output} ({new FileInfo(output).Length / 1024} KB)");
            // The part the mod embeds: the distinct FSM structures ("name|hash"), next to the full inventory.
            string keys = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "fsmkeys.txt");
            using (StreamWriter w = new StreamWriter(keys, false, new UTF8Encoding(false))) InventoryIO.WriteFsmKeys(inv, w);
            Console.WriteLine($"written {keys} ({new FileInfo(keys).Length / 1024} KB)");
            return 0;
        }

        static int Diff(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("a", out string a) || !o.TryGetValue("b", out string b)) return Usage();
            Inventory ia = Load(a), ib = Load(b);
            int onlyA = 0, onlyB = 0, objChanged = 0, fsmChanged = 0, shown = 0;
            void Show(string s) { if (shown++ < 40) Console.WriteLine("  " + s); }
            foreach (InventoryObject x in ia.Objects)
            {
                InventoryObject y = ib.Find(x.Path);
                if (y == null) { onlyA++; Show("only in A: " + x.Path); continue; }
                if (x.ObjectHash() != y.ObjectHash()) { objChanged++; Show($"object: {x.Path}: [{string.Join(",", x.Components)}] {x.Children} -> [{string.Join(",", y.Components)}] {y.Children}"); }
                foreach (InventoryFsm f in x.Fsms)
                {
                    InventoryFsm g = y.Fsms.Find(z => z.Name == f.Name && z.Index == f.Index);
                    if (g == null || g.Hash != f.Hash) { fsmChanged++; Show($"fsm: {x.Path}#{f.Name}~{f.Index}: {f.Hash} -> {(g == null ? "missing" : g.Hash)}"); }
                }
            }
            foreach (InventoryObject y in ib.Objects) if (ia.Find(y.Path) == null) { onlyB++; Show("only in B: " + y.Path); }
            Console.WriteLine($"A: {InventoryIO.Summary(ia)}");
            Console.WriteLine($"B: {InventoryIO.Summary(ib)}");
            Console.WriteLine($"only in A {onlyA}, only in B {onlyB}, objects changed {objChanged}, FSMs changed {fsmChanged}");
            return onlyA + onlyB + objChanged + fsmChanged == 0 ? 0 : 1;
        }

        /// <summary>An inventory.json, or a raw dump (.jsonl) built on the fly.</summary>
        static Inventory Load(string path)
        {
            using StreamReader r = new StreamReader(path, Encoding.UTF8);
            if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                List<string> problems = new List<string>();
                Inventory inv = InventoryIO.FromDump(r, problems);
                if (problems.Count > 0) Console.Error.WriteLine($"{path}: {problems.Count} problems (first: {problems[0]})");
                return inv;
            }
            return InventoryIO.Read(r);
        }

        static Dictionary<string, string> Options(string[] args, int start)
        {
            Dictionary<string, string> o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unexpected argument " + args[i]);
                string key = args[i].Substring(2);
                if (i + 1 >= args.Length) throw new ArgumentException("--" + key + " needs a value");
                o[key] = args[++i];
            }
            return o;
        }
    }
}
