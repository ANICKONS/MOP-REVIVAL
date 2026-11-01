// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Comparison of one checkpoint (design §10.4, requirement 3.4): one or more vanilla runs (A = the first; the others
// measure what is unstable without any mod) and the mod M. For every key of all snapshots:
//   1. M same as one of the vanilla runs (Tolerances)        -> Match
//   2. the vanilla runs disagree (value, or key presence),    -> Noise (unstable in vanilla itself)
//      or a Volatile tolerance rule covers the key (random / timing-driven systems; Reason says which)
//      or the key varied between vanilla runs in an earlier calibration (NoiseModel; Reason "learned: ...")
//   3. listed in the expected differences                     -> Explained
//   4. otherwise                                              -> Unexplained (value differs), OnlyVanilla, OnlyMod
// With a single vanilla run there is no noise class: everything that differs is explained or unexplained.
// The game is not frame-exact between runs (its own load takes a varying number of frames), so values that change
// in steps over time differ between some vanilla runs and not others: more vanilla runs catch more of that noise.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Snapshot
{
    public enum DiffClass : byte
    {
        Match,
        Noise,
        Explained,
        Unexplained,
        OnlyVanilla,
        OnlyMod
    }

    public sealed class DiffRecord
    {
        public string Key;
        public DiffClass Class;
        /// <summary>A = first vanilla run, B = the first other vanilla run that differs from A (else the second run),
        /// M = the mod; null = key missing in that snapshot.</summary>
        public string A, B, M;
        public string Reason;           // Explained: the reason from the expected list

        public override string ToString() => Class + " " + Key + ": A=" + (A ?? "<none>") + " B=" + (B ?? "<none>") + " M=" + (M ?? "<none>");
    }

    public sealed class CompareResult
    {
        public readonly int[] Counts = new int[6];
        /// <summary>Every record that is not a Match, sorted by key.</summary>
        public readonly List<DiffRecord> Records = new List<DiffRecord>();

        public int Count(DiffClass c) => Counts[(int)c];

        /// <summary>Unexplained + OnlyVanilla + OnlyMod: each of them fails the run.</summary>
        public int Failures => Counts[(int)DiffClass.Unexplained] + Counts[(int)DiffClass.OnlyVanilla] + Counts[(int)DiffClass.OnlyMod];

        public IEnumerable<DiffRecord> Of(DiffClass c)
        {
            foreach (DiffRecord r in Records) if (r.Class == c) yield return r;
        }
    }

    public static class SnapshotComparer
    {
        /// <param name="b">Second vanilla run; null = no noise estimate.</param>
        public static CompareResult Compare(SnapshotData a, SnapshotData b, SnapshotData m, Tolerances tolerances,
                                            ExpectedDiffs expected, string scenario)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            List<SnapshotData> vanilla = new List<SnapshotData> { a };
            if (b != null) vanilla.Add(b);
            return Compare(vanilla, m, tolerances, expected, scenario);
        }

        /// <param name="vanilla">Vanilla runs, at least one; the first is the reference "A".</param>
        public static CompareResult Compare(IList<SnapshotData> vanilla, SnapshotData m, Tolerances tolerances,
                                            ExpectedDiffs expected, string scenario)
            => Compare(vanilla, m, tolerances, expected, scenario, null);

        /// <param name="learned">Keys that varied between vanilla runs in earlier calibrations (NoiseModel); null = none.</param>
        public static CompareResult Compare(IList<SnapshotData> vanilla, SnapshotData m, Tolerances tolerances,
                                            ExpectedDiffs expected, string scenario, NoiseModel learned)
        {
            if (vanilla == null || vanilla.Count == 0) throw new ArgumentException("At least one vanilla run is needed.", nameof(vanilla));
            for (int i = 0; i < vanilla.Count; i++)
                if (vanilla[i] == null) throw new ArgumentNullException(nameof(vanilla), "Vanilla run " + i + " is null.");
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (tolerances == null) tolerances = new Tolerances();

            CompareResult result = new CompareResult();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SnapshotData v in vanilla) keys.UnionWith(v.Values.Keys);
            keys.UnionWith(m.Values.Keys);

            int n = vanilla.Count;
            string[] values = new string[n];
            bool[] present = new bool[n];
            foreach (string key in keys)
            {
                string vm;
                bool inM = m.Values.TryGetValue(key, out vm);
                bool match = false;
                for (int i = 0; i < n; i++)
                {
                    present[i] = vanilla[i].Values.TryGetValue(key, out values[i]);
                    if (inM && present[i] && !match && tolerances.Same(key, values[i], vm)) match = true;
                }
                if (match)
                {
                    result.Counts[(int)DiffClass.Match]++;
                    continue;
                }

                // Do the vanilla runs agree with the first one?
                int other = n > 1 ? 1 : -1;
                bool noise = false;
                for (int i = 1; i < n; i++)
                {
                    bool differs = present[i] != present[0] || (present[0] && !tolerances.Same(key, values[0], values[i]));
                    if (!differs) continue;
                    noise = true;
                    other = i;
                    break;
                }

                DiffRecord rec = new DiffRecord
                {
                    Key = key,
                    A = present[0] ? values[0] : null,
                    B = other > 0 && present[other] ? values[other] : null,
                    M = inM ? vm : null
                };
                ToleranceRule vol = noise ? null : tolerances.VolatileRuleFor(key);
                NoiseEntry seen = noise || vol != null || learned == null ? null : learned.Find(key);
                if (noise) rec.Class = DiffClass.Noise;
                else if (vol != null)
                {
                    rec.Class = DiffClass.Noise;
                    rec.Reason = "volatile: " + vol.Reason;
                }
                else if (seen != null)
                {
                    rec.Class = DiffClass.Noise;
                    rec.Reason = "learned: varied in vanilla (" + string.Join(", ", seen.Seen.ToArray()) + ")";
                }
                else
                {
                    ExpectedDiff e = expected?.Explain(key, scenario);
                    if (e != null)
                    {
                        rec.Class = DiffClass.Explained;
                        rec.Reason = e.Reason;
                    }
                    else if (!inM) rec.Class = DiffClass.OnlyVanilla;
                    else if (!present[0]) rec.Class = DiffClass.OnlyMod;
                    else rec.Class = DiffClass.Unexplained;
                }
                result.Counts[(int)rec.Class]++;
                result.Records.Add(rec);
            }

            result.Records.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return result;
        }
    }
}
