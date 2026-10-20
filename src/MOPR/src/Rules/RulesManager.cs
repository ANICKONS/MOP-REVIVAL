// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Rules (5.1): finds every rule source, decides which ones apply, compiles them into the RuleBook MOP asks.
//
// Sources, in this order:
//   * built-in  - src/MOPR/Rules/<ModID>.json embedded in the DLL; applies while that mod is installed and enabled.
//                 Never copied to disk, so an update of MOPR always brings the current version.
//   * yours     - Mod Settings/MOPR/Rules/*.json. With "mod": only while that mod is enabled; without: always.
//                 "replaceBundled": true switches the built-in rules of that mod off.
//   * old files - Mod Settings/MOPR/<ModID>.mopconfig and Custom.txt (MOP 3.10 / MOPR 5.0). Still read; an old
//                 <ModID>.mopconfig replaces the built-in rules of that mod, as it did before.
//
// Files are read in the main menu and again at every game load, so an edited file or a mod switched on or off
// in the menu applies at the next load without pressing anything. A reload during a game only shows the result.
//
// Migration from 5.0: MOPR 5.0 copied its bundled .mopconfig files into the settings folder and recorded their MD5
// (MopData.BundledRuleHashes). A copy that is still byte-for-byte what MOPR wrote is deleted (the built-in JSON
// replaces it); an edited or unknown file is never touched and keeps working as an old-format file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MSCLoader;

using MOP.Common;
using MOP.Localization;

namespace MOP.Rules
{
    static class RulesManager
    {
        const string ResourcePrefix = "MOPR.Rules.";
        public const string FolderName = "Rules";

        /// <summary>The rules MOP uses. Never null.</summary>
        public static RuleBook Book { get; private set; } = RuleBook.Empty;

        /// <summary>The last read; differs from Book after a reload during a game (it applies at the next load).</summary>
        public static RuleBook Latest { get; private set; } = RuleBook.Empty;

        public static bool Loaded { get; private set; }

        /// <summary>Console "load-rules false": no rule applies (for testing).</summary>
        public static bool LoadRules = true;

        /// <summary>Console "reload all": every source applies, installed mod or not (for testing rule files).</summary>
        public static bool ActivateAll;

        public static string Folder => string.IsNullOrEmpty(MOP.ModConfigPath) ? null : Path.Combine(MOP.ModConfigPath, FolderName);

        static string lastIssueDigest;

        // ===== Loading ===================================================================================

        /// <param name="apply">true: MOP uses the result (menu, game load). false: read and report only (during a game).</param>
        public static void Load(bool apply)
        {
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                RuleBook book = Read();
                watch.Stop();
                Latest = book;
                if (apply) Book = book;
                Loaded = true;
                LogSummary(book, apply, watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RULES_LOAD_ERROR");
            }

            try { MopMenu.RefreshRulesStatus(); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "RULES_STATUS_ERROR"); }
        }

        static RuleBook Read()
        {
            List<RuleSet> bundled = ReadBundled();
            List<RuleSet> user = new List<RuleSet>();
            List<RuleSet> legacy = new List<RuleSet>();

            string config = MOP.ModConfigPath;
            if (!string.IsNullOrEmpty(config) && Directory.Exists(config))
            {
                MigrateLegacy(config);

                string folder = Folder;
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { ModConsole.Warn("con.rules.folder_failed", ex.Message); }

                if (Directory.Exists(folder))
                    foreach (string path in SortedFiles(folder, "*" + RuleJson.Extension))
                        user.Add(ReadFile(path, RuleSource.User));

                foreach (string path in SortedFiles(config, "*" + LegacyRules.Extension))
                    legacy.Add(ReadFile(path, RuleSource.Legacy));
                string custom = Path.Combine(config, LegacyRules.CustomFile);
                if (File.Exists(custom))
                    legacy.Add(ReadFile(custom, RuleSource.Legacy));
            }

            List<RuleSet> active = new List<RuleSet>(), inactive = new List<RuleSet>();

            // Which mods have their built-in rules replaced by a file of the user.
            Dictionary<string, string> replacedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (RuleSet s in user)
                if (s.ReplaceBundled && !s.Unreadable && !string.IsNullOrEmpty(s.Mod) && !replacedBy.ContainsKey(s.Mod))
                    replacedBy[s.Mod] = "your " + s.Name;
            foreach (RuleSet s in legacy)
                if (!s.Unreadable && !string.IsNullOrEmpty(s.Mod) && !replacedBy.ContainsKey(s.Mod))
                    replacedBy[s.Mod] = "the old-format " + s.Name;

            foreach (RuleSet s in bundled)
            {
                if (replacedBy.TryGetValue(s.Mod ?? "", out string by)) s.InactiveReason = "replaced by " + by;
                Decide(s, active, inactive);
            }
            foreach (RuleSet s in user) Decide(s, active, inactive);
            foreach (RuleSet s in legacy) Decide(s, active, inactive);

            return RuleBook.Compile(active, inactive);
        }

        static void Decide(RuleSet s, List<RuleSet> active, List<RuleSet> inactive)
        {
            if (s.InactiveReason == null)
            {
                if (s.Unreadable) s.InactiveReason = "could not be read";
                else if (!LoadRules) s.InactiveReason = "rules are off (mopr load-rules)";
                else if (!string.IsNullOrEmpty(s.Mod) && !ActivateAll) s.InactiveReason = ModState(s.Mod);
            }
            (s.InactiveReason == null ? active : inactive).Add(s);
        }

        /// <summary>null if the mod is installed and enabled, else why not.</summary>
        static string ModState(string modId)
        {
            if (ModLoader.LoadedMods == null) return "mod " + modId + " is not installed";
            foreach (Mod m in ModLoader.LoadedMods)
            {
                if (m == null || !string.Equals(m.ID, modId, StringComparison.OrdinalIgnoreCase)) continue;
                return m.isDisabled ? "mod " + modId + " is disabled" : null;
            }
            return "mod " + modId + " is not installed";
        }

        static List<RuleSet> ReadBundled()
        {
            List<RuleSet> sets = new List<RuleSet>();
            Assembly asm = Assembly.GetExecutingAssembly();
            List<string> names = new List<string>();
            foreach (string n in asm.GetManifestResourceNames())
                if (n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(RuleJson.Extension, StringComparison.OrdinalIgnoreCase))
                    names.Add(n);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string resource in names)
            {
                string file = resource.Substring(ResourcePrefix.Length);
                RuleSet set;
                try
                {
                    set = RuleJson.Read(ReadResource(asm, resource), file, RuleSource.Bundled);
                }
                catch (Exception ex)
                {
                    set = new RuleSet { Name = file, Source = RuleSource.Bundled, Unreadable = true };
                    set.Error(0, ex.Message);
                }
                sets.Add(set);
            }
            return sets;
        }

        /// <summary>The built-in rule file of a mod as text, or null.</summary>
        static string BundledText(string modId, out string fileName)
        {
            fileName = null;
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (!n.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                string file = n.Substring(ResourcePrefix.Length);
                if (string.Equals(file, modId + RuleJson.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = file;
                    return ReadResource(asm, n);
                }
            }
            return null;
        }

        static string ReadResource(Assembly asm, string name)
        {
            using (Stream s = asm.GetManifestResourceStream(name))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static RuleSet ReadFile(string path, RuleSource source)
        {
            string name = Path.GetFileName(path);
            RuleSet set;
            try
            {
                // UTF-8 always (a file without a BOM is not read in the system code page); a BOM is skipped.
                set = source == RuleSource.Legacy
                    ? LegacyRules.Read(File.ReadAllLines(path, Encoding.UTF8), name)
                    : RuleJson.Read(File.ReadAllText(path, Encoding.UTF8), name, source);
            }
            catch (Exception ex)
            {
                set = new RuleSet { Name = name, Source = source, Unreadable = true };
                set.Error(0, "could not read the file: " + ex.Message);
            }
            set.Path = path;
            return set;
        }

        static List<string> SortedFiles(string folder, string pattern)
        {
            List<string> files = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(folder, pattern))
                {
                    string name = Path.GetFileName(f);
                    // "._name" files are macOS metadata, not rule files. The pattern "*.json" also matches "*.json~".
                    if (name.StartsWith("._") || !name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(f);
                }
            }
            catch (Exception ex)
            {
                ModConsole.Warn("con.rules.list_failed", folder, ex.Message);
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        // ===== Migration =================================================================================

        static void MigrateLegacy(string config)
        {
            // MOP 3.10 used the mod ID "MOP": copy its Custom.txt once (it is then read as an old-format file).
            try
            {
                string target = Path.Combine(config, LegacyRules.CustomFile);
                string parent = Path.GetDirectoryName(config.TrimEnd('/', '\\'));
                string original = string.IsNullOrEmpty(parent) ? null : Path.Combine(Path.Combine(parent, "MOP"), LegacyRules.CustomFile);
                if (!File.Exists(target) && original != null && File.Exists(original))
                {
                    File.Copy(original, target);
                    ModConsole.Info("con.rules.custom_copied");
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "CUSTOM_RULES_MIGRATION");
            }

            // Copies MOPR 5.0 installed and nobody edited: the built-in JSON replaces them.
            MopData data;
            try { data = MopSettings.Data; }
            catch (Exception ex)
            {
                ModConsole.Warn("con.mopdata_read_failed", ex.Message);
                return;
            }
            if (data.BundledRuleHashes == null || data.BundledRuleHashes.Count == 0) return;

            int removed = 0;
            foreach (string name in new List<string>(data.BundledRuleHashes.Keys))
            {
                string path = Path.Combine(config, name);
                try
                {
                    if (File.Exists(path) && Md5(File.ReadAllBytes(path)) == data.BundledRuleHashes[name])
                    {
                        File.Delete(path);
                        removed++;
                    }
                    // Edited or gone: nothing to track any more either way.
                    data.BundledRuleHashes.Remove(name);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "RULES_MIGRATION_" + name);
                }
            }

            try { MopSettings.WriteData(data); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "RULES_MIGRATION_SAVE"); }
            if (removed > 0)
                ModConsole.Info("con.rules.migrated", removed);
        }

        static string Md5(byte[] bytes)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ===== Reporting =================================================================================

        static void LogSummary(RuleBook book, bool applied, double ms)
        {
            int bundled = 0, user = 0, legacy = 0;
            foreach (RuleSet s in book.Active)
            {
                if (s.Source == RuleSource.Bundled) bundled++;
                else if (s.Source == RuleSource.User) user++;
                else legacy++;
            }

            // One line in the console language, and the same line in English for the reports.
            ModConsole.InfoText(SummaryLine(book, applied, ms, bundled, user, legacy, Loc.Current),
                                SummaryLine(book, applied, ms, bundled, user, legacy, Language.English));

            List<RuleIssue> issues = AllIssues(book);
            StringBuilder digest = new StringBuilder();
            foreach (RuleIssue i in issues) digest.Append(i).Append('\n');
            if (digest.ToString() == lastIssueDigest) return;
            lastIssueDigest = digest.ToString();

            int shown = 0;
            foreach (RuleIssue i in issues)
            {
                if (shown == 8) break;
                // The issue itself (file, line, what is wrong) comes from the rule parser and stays English.
                if (i.IsError) ModConsole.Warn("con.rules.issue", i.ToString());
                else ModConsole.Info("con.rules.issue", MoprColors.Muted(i.ToString()));
                shown++;
            }
            if (issues.Count > shown)
                ModConsole.Info("con.rules.more_issues", issues.Count - shown);
        }

        static string SummaryLine(RuleBook book, bool applied, double ms, int bundled, int user, int legacy, Language lang)
        {
            StringBuilder line = new StringBuilder(Loc.TIn(lang, "con.rules.prefix"));
            if (book.Active.Count == 0) line.Append(MoprColors.Muted(Loc.TIn(lang, LoadRules ? "con.rules.none_needed" : "con.rules.off")));
            else
            {
                List<string> parts = new List<string>();
                if (bundled > 0) parts.Add(Loc.FIn(lang, "con.rules.n_builtin", bundled));
                if (user > 0) parts.Add(Loc.FIn(lang, "con.rules.n_yours", user));
                if (legacy > 0) parts.Add(Loc.FIn(lang, "con.rules.n_legacy", legacy));
                line.Append(Loc.FIn(lang, "con.rules.summary", MoprColors.Success(book.RuleCount.ToString()), book.Active.Count,
                                    string.Join(", ", parts.ToArray())));
            }
            if (ActivateAll) line.Append(MoprColors.Warning(Loc.TIn(lang, "con.rules.all_files")));
            if (!applied) line.Append(MoprColors.Warning(Loc.TIn(lang, "con.rules.next_load")));
            line.Append(MoprColors.Muted(Loc.FIn(lang, "con.rules.read_in", ms.ToString("0"))));
            return line.ToString();
        }

        /// <summary>Problems of the user's and old-format files (built-in files are checked before every release).</summary>
        static List<RuleIssue> AllIssues(RuleBook book)
        {
            List<RuleIssue> list = new List<RuleIssue>();
            foreach (RuleSet s in book.Active) list.AddRange(s.Issues);
            foreach (RuleSet s in book.Inactive)
                if (s.Source != RuleSource.Bundled || s.Unreadable) list.AddRange(s.Issues);
            return list;
        }

        static string SourceLabel(RuleSet s, Language lang)
        {
            switch (s.Source)
            {
                case RuleSource.Bundled: return Loc.TIn(lang, "con.rules.src_builtin");
                case RuleSource.User: return Loc.TIn(lang, "con.rules.src_yours");
                default: return Loc.TIn(lang, "con.rules.src_legacy");
            }
        }

        /// <summary>Console "mopr rules [all]" (in the console language).</summary>
        public static string Report(bool everything) => Report(everything, Loc.Current);

        /// <summary>
        /// The rules report in a given language (bug reports use English). The reasons why a file is not used and
        /// the problems come from the rule parser and stay English.
        /// </summary>
        public static string Report(bool everything, Language lang)
        {
            RuleBook book = Latest;
            StringBuilder sb = new StringBuilder();
            sb.Append(Loc.FIn(lang, "con.rules.report_head", book.Active.Count, book.RuleCount))
              .Append(Book != Latest ? Loc.TIn(lang, "con.rules.report_read_in_game") : "").Append('\n');
            sb.Append(Loc.FIn(lang, "con.rules.report_folder", Folder ?? "-")).Append('\n');

            if (book.Active.Count > 0)
            {
                sb.Append("\n<color=yellow>").Append(Loc.TIn(lang, "con.rules.report_active")).Append("</color>\n");
                foreach (RuleSet s in book.Active)
                    sb.Append("  ").Append(s.Name).Append(" (").Append(SourceLabel(s, lang)).Append(s.Mod != null ? ", " + s.Mod : "")
                      .Append(") - ").Append(Loc.FIn(lang, "con.rules.report_rules", s.RuleCount))
                      .Append(s.ErrorCount > 0 ? ", " + Loc.FIn(lang, "con.rules.report_problems", s.ErrorCount) : "").Append('\n');
            }

            int hiddenBundled = 0;
            List<string> inactive = new List<string>();
            foreach (RuleSet s in book.Inactive)
            {
                bool missingMod = s.Source == RuleSource.Bundled && s.InactiveReason != null && s.InactiveReason.EndsWith("is not installed");
                if (missingMod && !everything) { hiddenBundled++; continue; }
                inactive.Add("  " + s.Name + " (" + SourceLabel(s, lang) + ") - " + s.InactiveReason);
            }
            if (inactive.Count > 0 || hiddenBundled > 0)
            {
                sb.Append("\n<color=yellow>").Append(Loc.TIn(lang, "con.rules.report_unused")).Append("</color>\n");
                foreach (string l in inactive) sb.Append(l).Append('\n');
                if (hiddenBundled > 0)
                    sb.Append("  ").Append(Loc.FIn(lang, "con.rules.report_hidden", hiddenBundled)).Append('\n');
            }

            List<string> switches = new List<string>();
            if (book.SatsumaExclude) switches.Add(Loc.TIn(lang, "con.rules.sw_satsuma_excluded"));
            else if (book.SatsumaKeepRenderers) switches.Add(Loc.TIn(lang, "con.rules.sw_satsuma_renderers"));
            if (book.KeepEmptyBottles) switches.Add(Loc.TIn(lang, "con.rules.sw_bottles"));
            if (book.SkipFuryColliderFix) switches.Add(Loc.TIn(lang, "con.rules.sw_fury"));
            if (book.AllVehiclesPhysicsOnly) switches.Add(Loc.TIn(lang, "con.rules.sw_physics_only"));
            if (book.NoLods) switches.Add(Loc.TIn(lang, "con.rules.sw_no_lods"));
            if (switches.Count > 0)
                sb.Append("\n<color=yellow>").Append(Loc.TIn(lang, "con.rules.report_switches")).Append("</color>\n  ")
                  .Append(string.Join(", ", switches.ToArray())).Append('\n');

            List<RuleIssue> issues = AllIssues(book);
            if (issues.Count > 0)
            {
                sb.Append("\n<color=yellow>").Append(Loc.TIn(lang, "con.rules.report_issues")).Append("</color>\n");
                foreach (RuleIssue i in issues) sb.Append("  ").Append(i.IsError ? "" : Loc.TIn(lang, "con.rules.report_note")).Append(i).Append('\n');
            }

            if (ModLoader.CurrentScene == CurrentScene.Game && Hypervisor.Instance != null && Book == Latest)
            {
                List<string> unmatched = book.Unmatched();
                if (unmatched.Count > 0)
                {
                    sb.Append("\n<color=yellow>").Append(Loc.TIn(lang, "con.rules.report_unmatched")).Append("</color> ")
                      .Append(Loc.TIn(lang, "con.rules.report_unmatched_why")).Append('\n');
                    foreach (string u in unmatched) sb.Append("  ").Append(u).Append('\n');
                }
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Console "mopr rules why &lt;name&gt;".</summary>
        public static string Why(string name)
        {
            string why = Book.Why(name);
            return why.Length > 0 ? "<b>" + name + "</b>\n" + why : Loc.F("con.rules.why_none", name);
        }

        /// <summary>The "Mod compatibility" block of the settings page.</summary>
        public static string StatusText()
        {
            if (!Loaded) return MoprColors.Colorize(MoprColors.Hint, Loc.T("menu.rules_status_none"));
            RuleBook book = Latest;

            List<string> builtIn = new List<string>();
            List<string> lines = new List<string>();
            int problems = 0;
            foreach (RuleSet s in book.Active)
            {
                problems += s.ErrorCount;
                if (s.Source == RuleSource.Bundled) { builtIn.Add(s.Mod); continue; }
                string kind = Loc.T(s.Source == RuleSource.User ? "menu.rules_kind_user" : "menu.rules_kind_legacy");
                lines.Add("• " + s.Name + Loc.T("menu.rules_sep") + kind + ", " + Loc.Plural("menu.rules_count", s.RuleCount) +
                          (s.ErrorCount > 0 ? ", " + MoprColors.Warning(Loc.Plural("menu.rules_problems", s.ErrorCount)) : ""));
            }
            foreach (RuleSet s in book.Inactive)
            {
                if (s.Source == RuleSource.Bundled) continue;
                problems += s.ErrorCount;
                string why = s.Unreadable ? MoprColors.Warning(Loc.T("menu.rules_unreadable"))
                           : !LoadRules ? Loc.T("menu.rules_off")
                           : Loc.F("menu.rules_mod_missing", s.Mod);
                lines.Add("• " + s.Name + Loc.T("menu.rules_sep") + why);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(builtIn.Count > 0 ? Loc.F("menu.rules_builtin", string.Join(", ", builtIn.ToArray())) : Loc.T("menu.rules_builtin_none"));
            const int MaxLines = 6;
            for (int i = 0; i < lines.Count && i < MaxLines; i++) sb.Append('\n').Append(lines[i]);
            if (lines.Count > MaxLines) sb.Append('\n').Append(Loc.F("menu.rules_more", lines.Count - MaxLines));
            if (problems > 0) sb.Append('\n').Append(MoprColors.Warning(Loc.T("menu.rules_see_console")));
            if (Book != Latest) sb.Append('\n').Append(MoprColors.Colorize(MoprColors.Note, Loc.T("menu.rules_next_load")));
            return MoprColors.Colorize(MoprColors.Hint, sb.ToString());
        }

        // ===== Files of the user =========================================================================

        const string Template =
            "// Your own MOPR rules. Every key is optional: delete what you don't need.\n" +
            "// Names are exact object names from the game (case matters). Changes apply at the next game load;\n" +
            "// problems are listed by the console command \"mopr rules\".\n" +
            "{\n" +
            "  // The mod these rules are for (its ID in the MSCLoader mod list). Without \"mod\" the file always applies.\n" +
            "  // \"mod\": \"ModID\",\n" +
            "  // true: MOPR's built-in rules for that mod are not used, only this file.\n" +
            "  // \"replaceBundled\": true,\n" +
            "\n" +
            "  // MOPR never touches these objects.\n" +
            "  \"exclude\": [],\n" +
            "  // MOPR never switches these off (their scripts keep running). It may still hide a world object\n" +
            "  // or freeze an item's or a vehicle's physics.\n" +
            "  \"keepActive\": [],\n" +
            "  // Children that stay on while MOPR switches their place or vehicle off, e.g. { \"YARD\": [ \"Handle\" ] }.\n" +
            "  \"keepActiveIn\": {},\n" +
            "  // Objects of a mod that MOPR should switch off far away.\n" +
            "  \"manage\": { \"objects\": [], \"renderers\": [], \"items\": [], \"vehicles\": [], \"vehiclePhysics\": [] }\n" +
            "\n" +
            "  // Also possible:\n" +
            "  //   \"reparent\": { \"OBJECT\": \"NEW PARENT\" or null },   new parent at load (null = the scene root)\n" +
            "  //   \"noLod\": [ \"VEHICLE\" ],                           no simplified far-away copy of a vehicle\n" +
            "  //   \"sectors\": [ { \"position\": [x, y, z], \"size\": [x, y, z], \"rotation\": [x, y, z],\n" +
            "  //                \"keepVisible\": [ \"NAME\" ], \"drawDistance\": 300 } ]   an extra indoor area\n" +
            "  //   \"satsuma\": \"keepRenderers\" or \"exclude\"\n" +
            "  //   \"keepEmptyBottles\": true                           even with \"Remove empty bottles\" on\n" +
            "  //   \"skipFixes\": [ \"furyCollider\" ]\n" +
            "  //   \"allVehiclesPhysicsOnly\": true, \"noLods\": true    whole game; only in files without \"mod\"\n" +
            "  // Examples: \"mopr rules export <ModID>\" writes MOPR's built-in rules of a mod into this folder.\n" +
            "}\n";

        // 5.1: NewFile / Export / Convert print their result themselves (localized console line, English report copy).

        /// <summary>Creates a commented template (name without extension) and opens it.</summary>
        public static void NewFile(string name)
        {
            string folder = Folder;
            if (folder == null) { ModConsole.Info("con.rules.no_folder"); return; }
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, (string.IsNullOrEmpty(name) ? "MyRules" : name) + RuleJson.Extension);
            if (File.Exists(path))
            {
                Open(path);
                ModConsole.Info("con.rules.exists_opened", path);
                return;
            }
            File.WriteAllText(path, Template, new UTF8Encoding(false));
            Open(path);
            ModConsole.Info("con.rules.created", path);
        }

        /// <summary>Writes the built-in rules of a mod as your own file (it then replaces them).</summary>
        public static void Export(string modId)
        {
            string text = BundledText(modId, out string file);
            if (text == null) { ModConsole.Info("con.rules.no_builtin", modId); return; }
            string folder = Folder;
            if (folder == null) { ModConsole.Info("con.rules.no_folder"); return; }
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, file);
            if (File.Exists(path)) { ModConsole.Info("con.rules.exists", path); return; }

            RuleSet set = RuleJson.Read(text, file, RuleSource.Bundled);
            set.ReplaceBundled = true;
            File.WriteAllText(path, RuleJson.Write(set, new[]
            {
                "A copy of MOPR's built-in rules for the \"" + set.Mod + "\" mod. While this file exists, MOPR uses it instead",
                "of the built-in rules (\"replaceBundled\"). Delete it to go back to the built-in ones.",
            }), new UTF8Encoding(false));
            ModConsole.Info("con.rules.written", path);
        }

        /// <summary>Converts an old-format file to JSON with the same meaning; the old file is kept as .bak.</summary>
        public static void Convert(string name)
        {
            string config = MOP.ModConfigPath;
            if (string.IsNullOrEmpty(config)) { ModConsole.Info("con.rules.no_folder"); return; }
            if (!name.EndsWith(LegacyRules.Extension, StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                name += name.Equals("Custom", StringComparison.OrdinalIgnoreCase) ? ".txt" : LegacyRules.Extension;
            string source = Path.Combine(config, name);
            if (!File.Exists(source)) { ModConsole.Info("con.rules.no_legacy", name, config); return; }

            RuleSet set = LegacyRules.Read(File.ReadAllLines(source, Encoding.UTF8), Path.GetFileName(source));
            // An old <ModID>.mopconfig replaced MOPR's built-in rules of that mod: the JSON keeps doing that.
            if (set.Mod != null) set.ReplaceBundled = true;
            string folder = Folder;
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, (set.Mod ?? "Custom") + RuleJson.Extension);
            if (File.Exists(target)) { ModConsole.Info("con.rules.exists", target); return; }

            List<string> header = new List<string> { "Converted from " + set.Name + " (the old file is kept as " + set.Name + ".bak)." };
            foreach (RuleIssue i in set.Issues) header.Add((i.IsError ? "Skipped, line " : "Note, line ") + i.Line + ": " + i.Message);
            File.WriteAllText(target, RuleJson.Write(set, header), new UTF8Encoding(false));
            File.Copy(source, source + ".bak", true);
            File.Delete(source);
            if (set.Issues.Count > 0) ModConsole.Info("con.rules.converted_issues", target, set.Issues.Count);
            else ModConsole.Info("con.rules.written", target);
        }

        /// <summary>Path of a user file by name (with or without .json), an old-format file, or null.</summary>
        public static string FindFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<string> candidates = new List<string>();
            if (Folder != null)
            {
                candidates.Add(Path.Combine(Folder, name));
                candidates.Add(Path.Combine(Folder, name + RuleJson.Extension));
            }
            if (!string.IsNullOrEmpty(MOP.ModConfigPath))
            {
                candidates.Add(Path.Combine(MOP.ModConfigPath, name));
                candidates.Add(Path.Combine(MOP.ModConfigPath, name + LegacyRules.Extension));
                candidates.Add(Path.Combine(MOP.ModConfigPath, name + ".txt"));
            }
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>The text of a user or old-format file, else of the built-in rules of that mod; null if none.</summary>
        public static string FileText(string name)
        {
            string path = FindFile(name);
            if (path != null) return File.ReadAllText(path, Encoding.UTF8);
            string id = name.EndsWith(RuleJson.Extension, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - RuleJson.Extension.Length) : name;
            return BundledText(id, out _);
        }

        public static void OpenFolder()
        {
            string folder = Folder;
            if (folder == null) return;
            Directory.CreateDirectory(folder);
            Open(folder);
        }

        static void Open(string path)
        {
            try { Process.Start(path); }
            catch (Exception ex) { ModConsole.Warn("con.open_failed", path, ex.Message); }
        }

        // ===== Bug reports ===============================================================================

        /// <summary>The rule part of a bug report: active and unused sources, problems, and the user's own files.</summary>
        public static string ReportForBugReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(Report(true, Language.English), "<.*?>", ""));
            foreach (RuleSet s in Latest.Active) AppendFile(sb, s);
            foreach (RuleSet s in Latest.Inactive) AppendFile(sb, s);
            return sb.ToString();
        }

        static void AppendFile(StringBuilder sb, RuleSet s)
        {
            if (s.Source == RuleSource.Bundled || string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path)) return;
            try
            {
                string text = File.ReadAllText(s.Path, Encoding.UTF8);
                if (text.Length > 20000) text = text.Substring(0, 20000) + "\n[...]";
                sb.AppendLine().Append("=== ").Append(s.Name).AppendLine(" ===").AppendLine(text);
            }
            catch (Exception ex)
            {
                sb.AppendLine().Append("=== ").Append(s.Name).Append(" === (").Append(ex.Message).AppendLine(")");
            }
        }
    }
}
