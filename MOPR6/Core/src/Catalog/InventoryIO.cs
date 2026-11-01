// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Reading and writing the inventory (design §5):
//   * FromDump: the TestBot "inventory" dump of vanilla (JSON lines, TestBot\Inventory.cs) -> Inventory with
//     the FSM hashes computed here, by the same Fingerprint the mod uses in the game;
//   * Write / Read: the embedded Resources\catalog\inventory.json (one object per line, so a diff of two game versions
//     stays readable).
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.
// In the game only the plain-text ReadFsmKeys may be called: everything with JObject fails there with a
// TypeLoadException (Newtonsoft.Json.Linq needs System.ComponentModel.INotifyPropertyChanging, missing in the game's
// Mono profile; seen in the settings-check run). The JSON parts are for the host tools.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MOPR6.Core.Catalog
{
    public static class InventoryIO
    {
        /// <summary>
        /// Builds the inventory from a dump. Problems (bad lines, FSMs on unknown objects) are listed, not thrown: the
        /// tool shows them and refuses to write the file.
        /// </summary>
        public static Inventory FromDump(TextReader reader, List<string> problems)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (problems == null) throw new ArgumentNullException(nameof(problems));
            Inventory inv = new Inventory();
            Dictionary<string, InventoryObject> byPath = new Dictionary<string, InventoryObject>(StringComparer.Ordinal);
            List<KeyValuePair<string, InventoryFsm>> fsms = new List<KeyValuePair<string, InventoryFsm>>();
            string line;
            int n = 0;
            while ((line = reader.ReadLine()) != null)
            {
                n++;
                if (line.Length == 0) continue;
                JObject o;
                try { o = Parse(line); }
                catch (JsonException e) { problems.Add("line " + n + ": " + e.Message); continue; } // catch-ok: listed in problems, the tool refuses to write
                string t = (string)o["t"];
                if (t == "game") inv.GameAssemblyHash = (string)o["asm"];
                else if (t == "o")
                {
                    InventoryObject io = new InventoryObject { Path = (string)o["p"], Children = (int?)o["n"] ?? 0 };
                    if (string.IsNullOrEmpty(io.Path)) { problems.Add("line " + n + ": object without a path"); continue; }
                    foreach (JToken c in (o["c"] as JArray) ?? new JArray()) io.Components.Add((string)c);
                    if (byPath.ContainsKey(io.Path)) { problems.Add("line " + n + ": duplicate path " + io.Path); continue; }
                    byPath[io.Path] = io;
                    inv.Objects.Add(io);
                }
                else if (t == "f")
                {
                    FsmShape shape = Shape(o);
                    InventoryFsm f = new InventoryFsm { Name = shape.Name, Index = (int?)o["i"] ?? 0, Hash = Fingerprint.Of(shape) };
                    fsms.Add(new KeyValuePair<string, InventoryFsm>((string)o["p"], f));
                }
                else problems.Add("line " + n + ": unknown record '" + t + "'");
            }
            foreach (KeyValuePair<string, InventoryFsm> kv in fsms)
            {
                InventoryObject owner;
                if (kv.Key == null || !byPath.TryGetValue(kv.Key, out owner)) { problems.Add("FSM " + kv.Value.Name + " on an unknown object " + kv.Key); continue; }
                owner.Fsms.Add(kv.Value);
            }
            foreach (InventoryObject io in inv.Objects) io.Fsms.Sort((a, b) => a.Name == b.Name ? a.Index.CompareTo(b.Index) : string.CompareOrdinal(a.Name, b.Name));
            inv.Objects.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            inv.Invalidate();
            return inv;
        }

        /// <summary>The shape of one FSM record of the dump.</summary>
        public static FsmShape Shape(JObject o)
        {
            FsmShape f = new FsmShape { Name = (string)o["name"], StartState = (string)o["start"] };
            foreach (JToken s in (o["s"] as JArray) ?? new JArray())
            {
                StateShape st = new StateShape { Name = (string)s["n"] };
                foreach (JToken a in (s["a"] as JArray) ?? new JArray()) st.Actions.Add((string)a);
                foreach (JToken tr in (s["tr"] as JArray) ?? new JArray()) st.Transitions.Add((string)tr);
                f.States.Add(st);
            }
            foreach (JToken g in (o["g"] as JArray) ?? new JArray()) f.GlobalTransitions.Add((string)g);
            return f;
        }

        // inventory.json: a header line, then one line per object:
        //   {"game":"<md5>","created":"2026-10-28","objects":N}
        //   {"p":"YARD/Building/WOODSHED/Door","c":["BoxCollider","MeshRenderer","PlayMakerFSM"],"n":0,"f":[["Use",0,"<hash>"]]}

        public static void Write(Inventory inv, TextWriter w)
        {
            if (inv == null) throw new ArgumentNullException(nameof(inv));
            if (w == null) throw new ArgumentNullException(nameof(w));
            JObject head = new JObject { ["game"] = inv.GameAssemblyHash, ["created"] = inv.Created, ["objects"] = inv.Objects.Count };
            w.Write(head.ToString(Formatting.None));
            w.Write('\n');
            foreach (InventoryObject o in inv.Objects)
            {
                JObject j = new JObject { ["p"] = o.Path, ["c"] = new JArray(o.Components.ToArray()), ["n"] = o.Children };
                if (o.Fsms.Count > 0)
                {
                    JArray fs = new JArray();
                    foreach (InventoryFsm f in o.Fsms) fs.Add(new JArray(f.Name, f.Index, f.Hash));
                    j["f"] = fs;
                }
                w.Write(j.ToString(Formatting.None));
                w.Write('\n');
            }
        }

        public static Inventory Read(TextReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            Inventory inv = new Inventory();
            string line = r.ReadLine();
            if (line == null) throw new FormatException("inventory: empty file");
            JObject head = Parse(line);
            inv.GameAssemblyHash = (string)head["game"];
            inv.Created = (string)head["created"];
            int expected = (int?)head["objects"] ?? -1;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                JObject j = Parse(line);
                InventoryObject o = new InventoryObject { Path = (string)j["p"], Children = (int?)j["n"] ?? 0 };
                foreach (JToken c in (j["c"] as JArray) ?? new JArray()) o.Components.Add((string)c);
                foreach (JToken f in (j["f"] as JArray) ?? new JArray())
                    o.Fsms.Add(new InventoryFsm { Name = (string)f[0], Index = (int)f[1], Hash = (string)f[2] });
                inv.Objects.Add(o);
            }
            if (expected >= 0 && expected != inv.Objects.Count)
                throw new FormatException("inventory: " + inv.Objects.Count.ToString(CultureInfo.InvariantCulture) + " objects, header says " +
                                          expected.ToString(CultureInfo.InvariantCulture) + " (truncated file?)");
            return inv;
        }

        // fsmkeys.txt - what the mod embeds for "is this FSM vanilla" (the full inventory is 4 MB, the keys a few
        // hundred KB): a header "#game <md5> fsms <N> keys <K>", then the distinct "FsmName|hash" keys, sorted.

        public static string FsmKey(string fsmName, string hash) => fsmName + "|" + hash;

        public static void WriteFsmKeys(Inventory inv, TextWriter w)
        {
            if (inv == null) throw new ArgumentNullException(nameof(inv));
            if (w == null) throw new ArgumentNullException(nameof(w));
            List<string> keys = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int fsms = 0;
            foreach (InventoryObject o in inv.Objects)
                foreach (InventoryFsm f in o.Fsms)
                {
                    fsms++;
                    string k = FsmKey(f.Name, f.Hash);
                    if (seen.Add(k)) keys.Add(k);
                }
            keys.Sort(StringComparer.Ordinal);
            w.Write("#game " + (inv.GameAssemblyHash ?? "?") + " fsms " + fsms.ToString(CultureInfo.InvariantCulture) +
                    " keys " + keys.Count.ToString(CultureInfo.InvariantCulture) + "\n");
            foreach (string k in keys) { w.Write(k); w.Write('\n'); }
        }

        /// <summary>Reads fsmkeys.txt; <paramref name="gameHash"/> is the game hash of the header (null if missing).</summary>
        public static HashSet<string> ReadFsmKeys(TextReader r, out string gameHash)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            gameHash = null;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            int expected = -1;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    string[] p = line.Substring(1).Split(' ');
                    for (int i = 0; i + 1 < p.Length; i += 2)
                    {
                        if (p[i] == "game") gameHash = p[i + 1];
                        else if (p[i] == "keys") int.TryParse(p[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out expected);
                    }
                    continue;
                }
                keys.Add(line);
            }
            if (expected >= 0 && expected != keys.Count)
                throw new FormatException("fsmkeys: " + keys.Count.ToString(CultureInfo.InvariantCulture) + " keys, header says " +
                                          expected.ToString(CultureInfo.InvariantCulture) + " (truncated file?)");
            return keys;
        }

        /// <summary>One JSON object; strings stay strings (no date parsing of names such as "2026-10-28").</summary>
        static JObject Parse(string line)
        {
            using (JsonTextReader jr = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(jr);
        }

        /// <summary>Counts for the tool's summary line.</summary>
        public static string Summary(Inventory inv)
        {
            int fsms = 0, withFsm = 0;
            foreach (InventoryObject o in inv.Objects)
            {
                fsms += o.Fsms.Count;
                if (o.Fsms.Count > 0) withFsm++;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(inv.Objects.Count.ToString(CultureInfo.InvariantCulture)).Append(" objects, ")
              .Append(fsms.ToString(CultureInfo.InvariantCulture)).Append(" FSMs on ").Append(withFsm.ToString(CultureInfo.InvariantCulture))
              .Append(" objects, game ").Append(inv.GameAssemblyHash ?? "?");
            return sb.ToString();
        }
    }
}
