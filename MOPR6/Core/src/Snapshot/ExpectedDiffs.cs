// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The list of differences between the mod and vanilla that are explained (MOPR6\Equiv\expected.json, design
// §10.4): a key pattern, the scenarios it applies to, and the reason. Every entry counts its hits, so entries that
// no longer match anything are reported as stale.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Snapshot
{
    public sealed class ExpectedDiff
    {
        public string Pattern;
        /// <summary>Scenario name pattern; null or "*" = every scenario.</summary>
        public string Scenario;
        public string Reason;
        /// <summary>Version since which the difference exists (for the history, not used by the comparison).</summary>
        public string Since;

        public int Hits;

        Glob glob, scenarioGlob;
        internal bool Matches(string key, string scenario)
        {
            if (glob == null) glob = new Glob(Pattern ?? "");
            if (scenarioGlob == null) scenarioGlob = new Glob(string.IsNullOrEmpty(Scenario) ? "*" : Scenario);
            return glob.IsMatch(key) && scenarioGlob.IsMatch(scenario ?? "");
        }
    }

    public sealed class ExpectedDiffs
    {
        readonly List<ExpectedDiff> entries = new List<ExpectedDiff>();

        public ExpectedDiffs() { }

        public ExpectedDiffs(IEnumerable<ExpectedDiff> list)
        {
            if (list == null) return;
            foreach (ExpectedDiff e in list) Add(e);
        }

        public IList<ExpectedDiff> Entries => entries.AsReadOnly();

        public void Add(ExpectedDiff e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (string.IsNullOrEmpty(e.Pattern)) throw new ArgumentException("An expected difference needs a pattern.");
            if (string.IsNullOrEmpty(e.Reason)) throw new ArgumentException("Expected difference '" + e.Pattern + "' has no reason.");
            entries.Add(e);
        }

        /// <summary>The first entry that explains <paramref name="key"/> in <paramref name="scenario"/> (its hit count goes up).</summary>
        public ExpectedDiff Explain(string key, string scenario)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].Matches(key, scenario)) continue;
                entries[i].Hits++;
                return entries[i];
            }
            return null;
        }

        public List<ExpectedDiff> Stale()
        {
            List<ExpectedDiff> s = new List<ExpectedDiff>();
            foreach (ExpectedDiff e in entries) if (e.Hits == 0) s.Add(e);
            return s;
        }
    }
}
