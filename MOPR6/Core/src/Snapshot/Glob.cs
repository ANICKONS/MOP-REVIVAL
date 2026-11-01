// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Wildcard patterns for snapshot keys, tolerance rules and expected differences: '*' = any characters (also '/'),
// '?' = one character, everything else literal and case-sensitive. A pattern without wildcards is an exact key.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;

namespace MOPR6.Core.Snapshot
{
    public sealed class Glob
    {
        public readonly string Pattern;
        readonly bool literal;
        readonly string prefix;   // literal characters before the first wildcard (fast reject)

        public Glob(string pattern)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            int w = pattern.IndexOfAny(new[] { '*', '?' });
            literal = w < 0;
            prefix = literal ? pattern : pattern.Substring(0, w);
        }

        public bool IsMatch(string text)
        {
            if (text == null) return false;
            if (literal) return string.Equals(text, Pattern, StringComparison.Ordinal);
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return Match(Pattern, prefix.Length, text, prefix.Length);
        }

        // Iterative matching with backtracking to the last '*' (linear in practice).
        static bool Match(string p, int pi, string t, int ti)
        {
            int star = -1, mark = 0;
            while (ti < t.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || (p[pi] != '*' && p[pi] == t[ti])))
                {
                    pi++;
                    ti++;
                }
                else if (pi < p.Length && p[pi] == '*')
                {
                    star = pi++;
                    mark = ti;
                }
                else if (star >= 0)
                {
                    pi = star + 1;
                    ti = ++mark;
                }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }

        public override string ToString() => Pattern;
    }
}
