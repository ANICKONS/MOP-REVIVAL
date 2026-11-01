// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// A copy of the save folder as a snapshot (requirement 3.2): every value of every ES2 file becomes one key
//   save:<file>|<entry key>!<kind>      kind: b bool, i integer, f float, s string, v vector, q quaternion, c colour, raw
// so tolerance rules can address value types ("save:*!q" = every quaternion). Files that are not ES2 are compared
// line by line ("save:<file>:<line number>").
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MOPR6.Core.Es2;

namespace MOPR6.Core.Snapshot
{
    public static class SaveSnapshot
    {
        /// <summary>Files of a save folder that are not save data.</summary>
        public static bool IsIgnoredFile(string fileName) =>
            fileName.Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("README.txt", StringComparison.OrdinalIgnoreCase);

        public static string KindSuffix(Es2ValueKind kind)
        {
            switch (kind)
            {
                case Es2ValueKind.Bool: return "!b";
                case Es2ValueKind.Integer: return "!i";
                case Es2ValueKind.Float: return "!f";
                case Es2ValueKind.String: return "!s";
                case Es2ValueKind.Vector: return "!v";
                case Es2ValueKind.Quaternion: return "!q";
                case Es2ValueKind.Color: return "!c";
                default: return "!raw";
            }
        }

        /// <summary>Adds one save file to <paramref name="into"/>; returns the parser's warnings (empty for text files).</summary>
        public static List<string> AddFile(SnapshotData into, string fileName, byte[] data)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            List<string> warnings = new List<string>();
            Es2ParseResult r = Es2Parser.Parse(data);
            if (r.NotEs2)
            {
                string text = new UTF8Encoding(false, false).GetString(data);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    into.Add("save:" + fileName + ":" + (i + 1).ToString(CultureInfo.InvariantCulture), lines[i]);
                return warnings;
            }
            foreach (Es2Entry e in r.Entries)
                into.Add("save:" + fileName + "|" + e.Key + KindSuffix(e.Kind), e.Text);
            foreach (string w in r.Warnings) warnings.Add(fileName + ": " + w);
            if (r.Error != null) warnings.Add(fileName + ": " + r.Error);
            return warnings;
        }

        /// <summary>Every save file of a folder (no subfolders).</summary>
        public static SnapshotData FromFolder(string folder, List<string> warnings)
        {
            SnapshotData d = new SnapshotData();
            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (IsIgnoredFile(name)) continue;
                List<string> w = AddFile(d, name, File.ReadAllBytes(f));
                if (warnings != null) warnings.AddRange(w);
            }
            return d;
        }
    }
}
