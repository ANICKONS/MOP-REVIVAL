// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// "mopr report" and the settings button (requirement 12.3, design §12): Mods\Config\Mod Settings\MOPR6\
// report-<date>.txt, always in English. Version and preview mark, game hash against the inventory, the settings,
// every system with its mode, counters and verify mismatches, ledger and save barrier, the session's errors with their
// counts, the catalog matching and the IDs of the other mods. No save content; the user's profile folder and name are
// replaced (Core.Diagnostics.Redact).

using System;
using System.IO;
using System.Text;
using MOPR6.Core.Diagnostics;
using MOPR6.Core.Errors;
using MOPR6.Host;
using MOPR6.Testing;
using MOPR6.UI;
using MSCLoader;
using UnityEngine;

namespace MOPR6.Diagnostics
{
    internal static class BugReport
    {
        /// <summary>Writes the report. Null on success (path = the file), else the reason.</summary>
        internal static string Write(out string path)
        {
            path = null;
            try
            {
                string folder = ModLoader.GetModSettingsFolder(MoprMod.Instance);
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, Build(), new UTF8Encoding(false));
                ModLog.Line("bug report written: " + path);
                return null;
            }
            catch (Exception ex)
            {
                ModLog.Report("report/Write", ex, path);
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>The report text (English, redacted).</summary>
        internal static string Build()
        {
            StringBuilder sb = new StringBuilder();
            Core.ModVersion v = Core.ModVersion.Parse(BuildInfo.Version);
            sb.Append("MOPR6 bug report, ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC\n");
            sb.Append("Version: ").Append(BuildInfo.Version).Append(v.IsPreview ? " (PREVIEW)" : "").Append('\n');
            Catalog.Vanilla.Load();   // in the main menu it is not loaded yet; cheap (66 KB, once)
            sb.Append("Game: ").Append(Catalog.Vanilla.GameLine()).Append('\n');
            sb.Append("Unity ").Append(Safe(() => Application.unityVersion)).Append(", MSCLoader ").Append(Safe(() => ModLoader.MSCLoader_Ver))
              .Append(", ").Append(Safe(() => SystemInfo.operatingSystem)).Append('\n');
            sb.Append("Scene: ").Append(Safe(() => ModLoader.CurrentScene.ToString())).Append(", session time ")
              .Append(ModLog.Now().ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s\n");
            sb.Append("MOPR 5.x loaded: ").Append(MoprMod.Conflict5x ? "YES (MOPR6 inactive)" : "no").Append('\n');
            sb.Append("Settings: ").Append(SettingsPage.Describe()).Append('\n');

            sb.Append("\n== Systems\n").Append(SystemHost.Status());
            sb.Append("Verify mismatches: ").Append(SystemHost.Mismatches()).Append('\n');

            sb.Append("\n== Ledger and save barrier\n");
            sb.Append("held changes: ").Append(TestApi.LedgerCount()).Append(" (").Append(Ledger.Arbiters.Summary()).Append("); barrier: ")
              .Append(TestApi.Barrier()).Append('\n');

            sb.Append("\n== Errors (").Append(ModLog.Errors.Total).Append(" in total, ").Append(ModLog.Errors.Records.Count).Append(" distinct")
              .Append(ModLog.Errors.Dropped > 0 ? ", " + ModLog.Errors.Dropped + " not kept" : "").Append(")\n");
            foreach (ErrorRecord r in ModLog.Errors.Records)
            {
                sb.Append(r).Append(" first ").Append(r.FirstTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" s, last ").Append(r.LastTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s\n");
                if (!string.IsNullOrEmpty(r.Stack)) sb.Append(Indent(r.Stack)).Append('\n');
            }

            sb.Append("\n== Catalog\n").Append(Catalog.Vanilla.Status()).Append('\n');

            sb.Append("\n== Other mods (IDs)\n");
            int others = 0;
            foreach (Mod m in ModLoader.LoadedMods)
            {
                if (m == null || m.ID == MoprMod.ModId || (m.ID ?? "").StartsWith("MSCLoader_", StringComparison.Ordinal)) continue;
                sb.Append(m.ID).Append(m.isDisabled ? " (disabled)" : "").Append('\n');
                others++;
            }
            if (others == 0) sb.Append("none\n");

            sb.Append("\nLog: ").Append(ModLog.FilePath ?? "-").Append('\n');
            return Redacted(sb.ToString());
        }

        /// <summary>A path or text as it may be shown or written: without the user's profile folder and name.</summary>
        internal static string Shown(string text) => Redacted(text);

        static string Redacted(string text)
        {
            string profile = Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.Personal));
            // Personal is <profile>\Documents on Windows; the profile is its parent.
            string root = Safe(() => Environment.GetEnvironmentVariable("USERPROFILE"));
            if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(profile)) root = Path.GetDirectoryName(profile);
            return Redact.Paths(text, root, Safe(() => Environment.UserName));
        }

        static string Indent(string stack)
        {
            string[] lines = stack.Replace("\r", "").Split('\n');
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("    ").Append(lines[i].Trim());
            }
            return sb.ToString();
        }

        static string Safe(Func<string> f)
        {
            try { return f() ?? ""; }
            catch (Exception) { return "?"; }   // catch-ok: a missing detail of the report is shown as "?"
        }
    }
}
