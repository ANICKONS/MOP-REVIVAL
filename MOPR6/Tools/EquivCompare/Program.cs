using System;
using System.Collections.Generic;
using System.IO;

namespace MOPR6.Tools.EquivCompare
{
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) return Usage();
            string[] rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "es2dump": return SaveDump.Run(rest);
                    case "equiv": return Equiv.Run(rest);
                    case "bench": return Bench.Run(rest);
                    case "catalog": return Catalog.Run(rest);
                    case "noise": return Noise.Run(rest);
                    default: return Usage();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                                       ex is System.Text.Json.JsonException || ex is Newtonsoft.Json.JsonException || ex is FormatException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 2;
            }
        }

        static int Usage()
        {
            Console.Error.WriteLine("EquivCompare es2dump <save file or folder> [output folder]");
            Console.Error.WriteLine("EquivCompare equiv --a DIR [--b DIR ...] --m DIR --scenario NAME --out DIR [--tolerances FILE] [--expected FILE] [--allow-input]");
            Console.Error.WriteLine("EquivCompare bench --vanilla DIR [...] --ref DIR [...] --cand DIR [...] --out DIR");
            Console.Error.WriteLine("EquivCompare catalog build --in DUMP.jsonl --out inventory.json | catalog diff --a OLD --b NEW");
            Console.Error.WriteLine("EquivCompare noise learn --runs runs.json [--runs ...] --out noise.json [--tolerances FILE] [--fresh]");
            return 2;
        }

        /// <summary>Files of a save folder in a fixed order, without the ones that are not save data.</summary>
        public static List<string> SaveFiles(string folder)
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(f);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }
    }
}
