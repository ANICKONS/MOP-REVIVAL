// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Benchmark statistics and the release gate (requirement 4.4, design §11). Every benchmark point is measured in
// several runs per variant; a point passes when MOPR6 is not slower than the reference (MOPR 5.1.1) beyond the
// measurement noise:
//   average FPS:   mean6 >= mean5 - max(2 * sigma, 1 % of mean5)
//   p99 frame ms:  mean6 <= mean5 + max(2 * sigma, 2 % of mean5)
// sigma = standard error of the difference of the two means, sqrt(s6^2/n6 + s5^2/n5) (sample standard deviations).
// With a single run per variant sigma is 0 and only the percentage margin applies.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MOPR6.Core.Bench
{
    public sealed class Sample
    {
        public readonly List<double> Values = new List<double>();

        public Sample() { }
        public Sample(IEnumerable<double> values) { if (values != null) Values.AddRange(values); }

        public int Count => Values.Count;

        public double Mean
        {
            get
            {
                if (Values.Count == 0) return double.NaN;
                double s = 0;
                foreach (double v in Values) s += v;
                return s / Values.Count;
            }
        }

        /// <summary>Sample standard deviation (n - 1); 0 for fewer than two values.</summary>
        public double StdDev
        {
            get
            {
                if (Values.Count < 2) return 0;
                double m = Mean, s = 0;
                foreach (double v in Values) s += (v - m) * (v - m);
                return Math.Sqrt(s / (Values.Count - 1));
            }
        }
    }

    public enum GateMetric : byte
    {
        AvgFps,      // higher is better
        P99Ms        // lower is better
    }

    public sealed class GateCheck
    {
        public string Point;
        public GateMetric Metric;
        public double Candidate, Reference, Sigma, Margin;
        /// <summary>Candidate minus reference, as a percentage of the reference (positive = higher value).</summary>
        public double DeltaPercent;
        public bool Pass;
        /// <summary>A variant has no measurement for this point.</summary>
        public bool Missing;

        public override string ToString()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (Missing) return Point + " " + Metric + ": MISSING";
            return string.Format(c, "{0} {1}: {2:0.00} vs {3:0.00} ({4:+0.0;-0.0}%), margin {5:0.00} -> {6}",
                Point, Metric, Candidate, Reference, DeltaPercent, Margin, Pass ? "pass" : "FAIL");
        }
    }

    public static class BenchGate
    {
        public const double FpsMarginPercent = 1.0;
        public const double P99MarginPercent = 2.0;

        public static GateCheck Check(string point, GateMetric metric, Sample candidate, Sample reference)
        {
            GateCheck g = new GateCheck { Point = point, Metric = metric };
            if (candidate == null || reference == null || candidate.Count == 0 || reference.Count == 0)
            {
                g.Missing = true;
                return g;
            }
            g.Candidate = candidate.Mean;
            g.Reference = reference.Mean;
            g.Sigma = Math.Sqrt(Sq(candidate.StdDev) / candidate.Count + Sq(reference.StdDev) / reference.Count);
            double percent = metric == GateMetric.AvgFps ? FpsMarginPercent : P99MarginPercent;
            g.Margin = Math.Max(2 * g.Sigma, percent / 100.0 * Math.Abs(g.Reference));
            g.DeltaPercent = g.Reference != 0 ? (g.Candidate - g.Reference) / g.Reference * 100.0 : 0;
            g.Pass = metric == GateMetric.AvgFps
                ? g.Candidate >= g.Reference - g.Margin
                : g.Candidate <= g.Reference + g.Margin;
            return g;
        }

        /// <summary>True when every check passed and none is missing.</summary>
        public static bool AllPass(IEnumerable<GateCheck> checks)
        {
            bool any = false;
            foreach (GateCheck g in checks)
            {
                any = true;
                if (g.Missing || !g.Pass) return false;
            }
            return any;
        }

        static double Sq(double x) => x * x;
    }
}
