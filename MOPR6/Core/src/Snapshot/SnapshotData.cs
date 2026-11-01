// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// A snapshot as the harness compares it: key -> value text, plus metadata lines ("#frame<TAB>1234").
// Written by the TestBot (fullsnap_*.tsv) or built from a save copy (Es2Parser entries, key "save:<file>|<tag>").
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.IO;

namespace MOPR6.Core.Snapshot
{
    public sealed class SnapshotData
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Keys that appeared more than once in the source (the last value is kept).</summary>
        public int DuplicateKeys;

        public int Count => Values.Count;

        public void Add(string key, string value)
        {
            if (Values.ContainsKey(key)) DuplicateKeys++;
            Values[key] = value ?? "";
        }

        /// <summary>Parses "key TAB value" lines; lines starting with '#' are metadata ("#name TAB value").</summary>
        public static SnapshotData Parse(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            SnapshotData d = new SnapshotData();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                string key = tab < 0 ? line : line.Substring(0, tab);
                string value = tab < 0 ? "" : line.Substring(tab + 1);
                if (key.Length > 0 && key[0] == '#') d.Meta[key.Substring(1)] = value;
                else d.Add(key, value);
            }
            return d;
        }

        public static SnapshotData Parse(string text)
        {
            using (StringReader r = new StringReader(text ?? "")) return Parse(r);
        }
    }
}
