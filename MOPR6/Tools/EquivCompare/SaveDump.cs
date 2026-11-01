using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MOPR6.Core.Es2;

namespace MOPR6.Tools.EquivCompare
{
    /// <summary>es2dump: every save file as sorted "key TAB kind TAB value" text, plus a summary on the console.</summary>
    static class SaveDump
    {
        public static int Run(string[] args)
        {
            if (args.Length < 1) { Console.Error.WriteLine("es2dump <save file or folder> [output folder]"); return 2; }
            string input = args[0];
            string outDir = args.Length > 1 ? args[1] : null;
            if (outDir != null) Directory.CreateDirectory(outDir);

            List<string> files = Directory.Exists(input) ? Program.SaveFiles(input) : new List<string> { input };
            int problems = 0;
            foreach (string file in files)
            {
                byte[] data = File.ReadAllBytes(file);
                Es2ParseResult r = Es2Parser.Parse(data);
                string name = Path.GetFileName(file);
                if (r.NotEs2)
                {
                    Console.WriteLine($"{name,-22} not ES2 ({data.Length} bytes)");
                    continue;
                }

                Dictionary<Es2ValueKind, int> kinds = new Dictionary<Es2ValueKind, int>();
                foreach (Es2Entry e in r.Entries) kinds[e.Kind] = (kinds.TryGetValue(e.Kind, out int n) ? n : 0) + 1;
                StringBuilder k = new StringBuilder();
                foreach (KeyValuePair<Es2ValueKind, int> kv in kinds) k.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                Console.WriteLine($"{name,-22} {r.Tags,5} tags, {r.Entries.Count,6} entries:{k}");
                foreach (string w in r.Warnings) { Console.WriteLine("    warning: " + w); problems++; }
                foreach (KeyValuePair<int, int> u in r.UnknownHashes)
                    Console.WriteLine($"    unknown type hash 0x{u.Key:X8}: {u.Value} tag(s) kept raw");
                if (r.Error != null) { Console.WriteLine("    ERROR: " + r.Error); problems++; }

                if (outDir != null)
                {
                    List<Es2Entry> sorted = new List<Es2Entry>(r.Entries);
                    sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    StringBuilder sb = new StringBuilder(sorted.Count * 48);
                    foreach (Es2Entry e in sorted)
                        sb.Append(e.Key).Append('\t').Append(e.Kind).Append('\t').Append(Escape(e.Text)).Append('\n');
                    File.WriteAllText(Path.Combine(outDir, name + ".tsv"), sb.ToString(), new UTF8Encoding(false));
                }
            }
            return problems == 0 ? 0 : 1;
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
