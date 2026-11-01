// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// When two snapshot values count as the same (design §10.4). A value is a list of groups separated by ';', each a
// list of components separated by ','. The first rule whose pattern matches the key decides:
//   * Abs / Rel     numeric components may differ by max(Abs, Rel * |larger|);
//   * QuatDeg       the group is a quaternion (x,y,z,w): the angle between the rotations may be up to QuatDeg
//                   (q and -q are the same rotation);
//   * Groups        per-group settings, in order (missing groups: the rule's own settings);
//   * Ignore        the key is not compared at all (only for values that are meaningless by nature);
//   * Volatile      a difference is noise even when the vanilla runs agree (random / timing-driven systems).
// No rule: components that are numbers on both sides must be numerically equal ("1" == "1.0"), text must match.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MOPR6.Core.Snapshot
{
    public sealed class ToleranceGroup
    {
        public double Abs;
        public double Rel;
        public double QuatDeg;
    }

    public sealed class ToleranceRule
    {
        public string Pattern;
        public double Abs;
        public double Rel;
        public double QuatDeg;
        public bool Ignore;
        /// <summary>
        /// The key belongs to a system that is random or timing-driven in vanilla itself (weather, NPCs, random
        /// events): a difference that the vanilla runs happen not to show is still classified as noise, not as a
        /// failure. Unlike Ignore it stays visible in the report.
        /// </summary>
        public bool Volatile;
        public List<ToleranceGroup> Groups;
        /// <summary>Why this tolerance exists (shown in reports).</summary>
        public string Reason;

        Glob glob;
        internal Glob Glob => glob ?? (glob = new Glob(Pattern ?? ""));

        internal ToleranceGroup GroupAt(int i)
        {
            if (Groups != null && i < Groups.Count && Groups[i] != null) return Groups[i];
            return new ToleranceGroup { Abs = Abs, Rel = Rel, QuatDeg = QuatDeg };
        }
    }

    public sealed class Tolerances
    {
        static readonly ToleranceGroup Exact = new ToleranceGroup();
        readonly List<ToleranceRule> rules = new List<ToleranceRule>();

        public Tolerances() { }

        public Tolerances(IEnumerable<ToleranceRule> rules)
        {
            if (rules == null) return;
            foreach (ToleranceRule r in rules) Add(r);
        }

        public IList<ToleranceRule> Rules => rules.AsReadOnly();

        public void Add(ToleranceRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (string.IsNullOrEmpty(rule.Pattern)) throw new ArgumentException("A tolerance rule needs a pattern.");
            if (rule.Abs < 0 || rule.Rel < 0 || rule.QuatDeg < 0) throw new ArgumentException("Negative tolerance in rule " + rule.Pattern);
            rules.Add(rule);
        }

        public ToleranceRule RuleFor(string key)
        {
            for (int i = 0; i < rules.Count; i++)
                if (!rules[i].Volatile && rules[i].Glob.IsMatch(key)) return rules[i];
            return null;
        }

        /// <summary>The Volatile rule that covers <paramref name="key"/>, or null. Volatile rules are separate from the
        /// tolerance rules: a key can have both (a tolerance for its value and the volatile class).</summary>
        public ToleranceRule VolatileRuleFor(string key)
        {
            for (int i = 0; i < rules.Count; i++)
                if (rules[i].Volatile && rules[i].Glob.IsMatch(key)) return rules[i];
            return null;
        }

        /// <summary>True when the two values of <paramref name="key"/> count as equal.</summary>
        public bool Same(string key, string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (a == null || b == null) return false;
            ToleranceRule rule = RuleFor(key);
            if (rule != null && rule.Ignore) return true;

            string[] ga = a.Split(';'), gb = b.Split(';');
            if (ga.Length != gb.Length) return false;
            for (int g = 0; g < ga.Length; g++)
            {
                ToleranceGroup tol = rule != null ? rule.GroupAt(g) : Exact;
                if (!SameGroup(ga[g], gb[g], tol)) return false;
            }
            return true;
        }

        static bool SameGroup(string a, string b, ToleranceGroup tol)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            string[] ca = a.Split(','), cb = b.Split(',');
            if (ca.Length != cb.Length) return false;

            if (tol.QuatDeg > 0 && ca.Length == 4)
            {
                double[] qa = new double[4], qb = new double[4];
                for (int i = 0; i < 4; i++)
                    if (!TryNumber(ca[i], out qa[i]) || !TryNumber(cb[i], out qb[i])) return false;
                return QuaternionAngleDeg(qa, qb) <= tol.QuatDeg;
            }

            for (int i = 0; i < ca.Length; i++)
            {
                if (string.Equals(ca[i], cb[i], StringComparison.Ordinal)) continue;
                double x, y;
                if (!TryNumber(ca[i], out x) || !TryNumber(cb[i], out y)) return false;
                if (double.IsNaN(x) || double.IsNaN(y)) { if (double.IsNaN(x) && double.IsNaN(y)) continue; return false; }
                if (x == y) continue;
                double limit = Math.Max(tol.Abs, tol.Rel * Math.Max(Math.Abs(x), Math.Abs(y)));
                if (!(Math.Abs(x - y) <= limit)) return false;
            }
            return true;
        }

        /// <summary>Angle in degrees between two rotations given as quaternions (not necessarily normalised).</summary>
        public static double QuaternionAngleDeg(double[] a, double[] b)
        {
            double na = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2] + a[3] * a[3]);
            double nb = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2] + b[3] * b[3]);
            if (na == 0 || nb == 0) return na == nb ? 0 : 180;
            double dot = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]) / (na * nb);
            dot = Math.Min(1, Math.Abs(dot));
            return 2 * Math.Acos(dot) * 180 / Math.PI;
        }

        static bool TryNumber(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
