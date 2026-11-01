// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Learned noise (task 11): keys that differed between vanilla runs of the same scenario in earlier calibrations.
// Three vanilla runs per comparison catch most of the noise, but not all: a value that changes by chance (the radio
// clip ends in real time, a random timer, a stack of boxes that settles one way or the other) is sometimes equal in
// all three and different in the mod run. The model pools every vanilla run the harness has seen, over all scenarios
// (a key unstable in one scenario is unstable by nature, wherever it is compared), so a difference on such a key is
// noise, not a failure. It is visible in the report with the scenarios where the key varied.
// Keys are normalised: "~k" sibling indices become "~*" (which of several identical objects varied is chance too).
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Text;

namespace MOPR6.Core.Snapshot
{
    public sealed class NoiseEntry
    {
        public string Key;
        /// <summary>Scenario / checkpoint pairs where the key varied ("eq-home-yard/fullsnap:yard").</summary>
        public List<string> Seen = new List<string>();
    }

    public sealed class NoiseModel
    {
        readonly Dictionary<string, NoiseEntry> entries = new Dictionary<string, NoiseEntry>(StringComparer.Ordinal);

        public int Count => entries.Count;
        /// <summary>Vanilla run groups the model was learned from (for the report).</summary>
        public int Groups;

        public IEnumerable<NoiseEntry> Entries => entries.Values;

        public static string Normalize(string key)
        {
            if (key == null) return null;
            if (key.IndexOf('~') < 0) return key;
            StringBuilder sb = new StringBuilder(key.Length);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                sb.Append(c);
                if (c != '~' || i + 1 >= key.Length || !char.IsDigit(key[i + 1])) continue;
                sb.Append('*');
                while (i + 1 < key.Length && char.IsDigit(key[i + 1])) i++;
            }
            return sb.ToString();
        }

        public NoiseEntry Find(string key)
        {
            NoiseEntry e;
            return entries.TryGetValue(Normalize(key), out e) ? e : null;
        }

        public void Add(string key, string seen)
        {
            string k = Normalize(key);
            NoiseEntry e;
            if (!entries.TryGetValue(k, out e)) entries[k] = e = new NoiseEntry { Key = k };
            if (seen != null && !e.Seen.Contains(seen) && e.Seen.Count < 8) e.Seen.Add(seen);
        }

        public void Add(NoiseEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key)) return;
            if (entry.Seen == null || entry.Seen.Count == 0) { Add(entry.Key, null); return; }
            foreach (string s in entry.Seen) Add(entry.Key, s);
        }

        /// <summary>
        /// Adds every key on which the vanilla runs of one checkpoint disagree (value or presence). Returns how many
        /// keys varied.
        /// </summary>
        public int Learn(IList<SnapshotData> vanilla, Tolerances tolerances, string where)
        {
            if (vanilla == null) throw new ArgumentNullException(nameof(vanilla));
            if (tolerances == null) tolerances = new Tolerances();
            if (vanilla.Count < 2) return 0;
            Groups++;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SnapshotData v in vanilla) keys.UnionWith(v.Values.Keys);
            int varied = 0;
            foreach (string key in keys)
            {
                string first;
                bool has0 = vanilla[0].Values.TryGetValue(key, out first);
                for (int i = 1; i < vanilla.Count; i++)
                {
                    string other;
                    bool has = vanilla[i].Values.TryGetValue(key, out other);
                    if (has == has0 && (!has || tolerances.Same(key, first, other))) continue;
                    Add(key, where);
                    varied++;
                    break;
                }
            }
            return varied;
        }
    }
}
