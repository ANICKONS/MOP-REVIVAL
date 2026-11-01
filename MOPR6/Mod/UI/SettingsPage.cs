// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The settings page (requirement 13, design §13): information, language, the main switch, the exact optimizations
// one by one, the verify mode, other. Every setting applies at once through the system host, in game too; none of
// the stage 1 settings needs a restart (13.3). Later stages add their sections (sleep, render, world, experimental).
//
// Language: the page is built before MSCLoader loads the saved values (ModSettings, then OnMenuLoad, then the values,
// then ModSettingsLoaded), so the build reads the saved language from MOPR6's own settings.json; ModSettingsLoaded
// then adopts the dropdown, which also covers "Reset settings" (as in MOPR 5.x).
// Setting IDs: "enabled", "verify", "checkUpdates", "language" and "sys.<system id>". MSCLoader saves them in
// Mods\Config\Mod Settings\MOPR6\settings.json; the files of 5.x (Mod Settings\MOPR) are only read by import5.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MOPR6.Core.Host;
using MOPR6.Diagnostics;
using MOPR6.Host;
using MOPR6.Localization;
using MSCLoader;
using UnityEngine;

namespace MOPR6.UI
{
    internal static class SettingsPage
    {
        internal const string GitHubLink = "https://github.com/ANICKONS/MOP-REVIVAL";

        static SettingsCheckBox enabled, verify, checkUpdates;
        static SettingsDropDownList languageSelect;
        static readonly Dictionary<string, SettingsCheckBox> systemBoxes = new Dictionary<string, SettingsCheckBox>();
        static bool built;
        /// <summary>The main switch when the page could not be built (the console still switches it, for this session).</summary>
        static bool mainWithoutPage = true;

        /// <summary>MSCLoader has applied the saved values (ModSettingsLoaded ran).</summary>
        internal static bool Loaded { get; private set; }

        internal static bool MainOn => enabled != null ? enabled.GetValue() : mainWithoutPage;
        internal static bool VerifyOn => verify != null && verify.GetValue();
        internal static bool CheckUpdates => checkUpdates == null || checkUpdates.GetValue();

        /// <summary>The page's choice for one system (its default when the page was not built).</summary>
        internal static bool SystemOn(Optimization s)
        {
            SettingsCheckBox box;
            return systemBoxes.TryGetValue(s.Id, out box) ? box.GetValue() : s.DefaultOn;
        }

        static readonly Color HeaderInfo = new Color32(0x2A, 0x45, 0x63, 0xFF);
        static readonly Color HeaderLanguage = new Color32(0x3B, 0x3B, 0x3B, 0xFF);
        static readonly Color HeaderTitle = new Color32(0xFF, 0xD3, 0x4A, 0xFF);
        const string HintHex = "#B0B0B0";
        const string WarnHex = "#FF7A1A";

        /// <summary>Builds the page (MSCLoader's ModSettings setup function; must run synchronously in it).</summary>
        internal static void Build()
        {
            LocalizedSettings.Clear();
            systemBoxes.Clear();
            Language system = Loc.DetectSystemLanguage();
            int savedLanguage = SavedDropDown("language");
            Loc.SetLanguage(savedLanguage >= 0 ? Loc.FromIndex(savedLanguage) : system);

            // Information. A real header first, so MSCLoader does not insert its own "Settings" header.
            Header("menu.header.info", HeaderInfo);
            Text(() => Loc.F("menu.info", BuildInfo.Version));
            if (Core.ModVersion.Parse(BuildInfo.Version).IsPreview) Text(() => Colorize(WarnHex, Loc.T("menu.preview")));
            if (MoprMod.Conflict5x) Text(() => Colorize(WarnHex, Loc.T("menu.conflict")));
            Button("menu.btn.report", OnReportButton);
            Button("menu.btn.github", () => OpenUrl(GitHubLink));

            Header("menu.header.language", HeaderLanguage);
            languageSelect = Settings.AddDropDownList("language", "", Loc.LanguageNames, (int)system, OnLanguageSelected);

            Header("menu.header.main");
            enabled = CheckBox("enabled", "menu.enabled", true, Apply);
            Hint("menu.enabled.hint");

            Header("menu.header.exact");
            Hint("menu.exact.hint");
            foreach (Optimization s in SystemHost.Systems)
            {
                if (s.Tier != Tier.Exact) continue;
                string key = "menu.sys." + s.Id;
                systemBoxes[s.Id] = CheckBox("sys." + s.Id, key, s.DefaultOn, Apply);
                Hint(key + ".hint");
            }
            verify = CheckBox("verify", "menu.verify", false, Apply);
            Hint("menu.verify.hint");

            Section("menu.header.sleep", "menu.sleep.hint", Tier.Sleep);
            Section("menu.header.world", "menu.world.hint", Tier.World);
            Section("menu.header.render", "menu.render.hint", Tier.Render);
            Section("menu.header.experimental", "menu.experimental.hint", Tier.Experimental);

            Header("menu.header.other");
            checkUpdates = CheckBox("checkUpdates", "menu.check_updates", true);
            Hint("menu.check_updates.hint");
            Button("menu.btn.import5", () => ModUI.ShowMessage(Import5.Run(Loc.Current), Loc.T("msg.title")));
            Hint("menu.import5.hint");
            built = true;
        }

        /// <summary>ModSettingsLoaded (also after "Reset settings"): the saved values are in.</summary>
        internal static void OnLoaded()
        {
            Loaded = true;
            ApplyLanguage(SelectedLanguage());
            Apply();
        }

        static readonly List<string> wanted = new List<string>();

        /// <summary>
        /// Brings the systems in line with the page. Only after the first game load (the hooks the systems rely on are
        /// installed then); in the main menu the values are just kept. The main switch off returns every change.
        /// </summary>
        internal static void Apply()
        {
            int returned, odd;
            if (Apply(out returned, out odd)) ModLog.Info(Loc.F("con.main_off", returned, odd));
        }

        /// <summary>True when the main switch stopped everything now (returned / odd: the ledger's changes).</summary>
        static bool Apply(out int returned, out int odd)
        {
            returned = odd = 0;
            if (MoprMod.Conflict5x || !SystemHost.Ready) return false;
            if (!MainOn)
            {
                int held = Ledger.Arbiters.Ledger != null ? Ledger.Arbiters.Ledger.Count : 0;
                bool any = held > 0;
                foreach (Optimization s in SystemHost.Systems)
                    if (s.State.Status == SystemStatus.Installed || s.State.Status == SystemStatus.Suspended) any = true;
                if (!any) return false;
                odd = SystemHost.StopAll();
                returned = held - odd;
                return true;
            }
            SystemMode mode = VerifyOn ? SystemMode.Verify : SystemMode.On;
            foreach (Optimization s in SystemHost.Systems)
            {
                if (s.Tier == Tier.Test) continue;   // the harness's probe
                // The verify mode is for the exact systems; render systems change the picture, which a verify cannot judge.
                if (SystemOn(s)) SystemHost.SetMode(s, s.Tier == Tier.Exact || s.Tier == Tier.Sleep ? mode : SystemMode.On);
                else SystemHost.Remove(s);
            }
            return false;
        }

        static bool appliedAtLoad;

        /// <summary>
        /// The first game load: the systems as the page says (defaults when the page was not built). Later loads keep
        /// what the session has (console overrides of the harness survive a reload).
        /// </summary>
        internal static void ApplyAtLoad()
        {
            if (appliedAtLoad) return;
            appliedAtLoad = true;
            Apply();
        }

        /// <summary>The main switch from the console: sets the checkbox like the user would, applies and saves it.</summary>
        internal static string SetMain(bool on, Language language)
        {
            if (enabled == null) mainWithoutPage = on;
            else if (enabled.GetValue() != on)
            {
                enabled.SetValue(on);
                Save();
            }
            int returned, odd;
            bool stopped = Apply(out returned, out odd);
            if (on) return Loc.FIn(language, "con.main_on");
            return stopped ? Loc.FIn(language, "con.main_off", returned, odd) : Loc.FIn(language, "con.main_off", 0, 0);
        }

        /// <summary>Sets a checkbox by ID (import5). False when the page has no such setting.</summary>
        internal static bool SetCheckBox(string id, bool value)
        {
            SettingsCheckBox box = null;
            if (id == "enabled") box = enabled;
            else if (id == "verify") box = verify;
            else if (id == "checkUpdates") box = checkUpdates;
            else if (id.StartsWith("sys.", StringComparison.Ordinal)) systemBoxes.TryGetValue(id.Substring(4), out box);
            if (box == null) return false;
            box.SetValue(value);
            return true;
        }

        internal static bool SetLanguageIndex(int index)
        {
            if (languageSelect == null || index < 0 || index >= Loc.LanguageNames.Length) return false;
            languageSelect.SetSelectedItemIndex(index);
            ApplyLanguage(Loc.FromIndex(index));
            return true;
        }

        /// <summary>The page values as one line for the bug report.</summary>
        internal static string Describe()
        {
            List<string> parts = new List<string>();
            parts.Add("main switch " + (MainOn ? "on" : "OFF"));
            parts.Add("verify " + (VerifyOn ? "on" : "off"));
            parts.Add("check updates " + (CheckUpdates ? "on" : "off"));
            parts.Add("language " + Loc.Current);
            foreach (KeyValuePair<string, SettingsCheckBox> kv in systemBoxes)
                parts.Add(kv.Key + " " + (kv.Value.GetValue() ? "on" : "off"));
            if (!built) parts.Add("(page not built: defaults)");
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Writes the page to settings.json now (MSCLoader's own save; it runs when the page closes).</summary>
        internal static bool Save()
        {
            try
            {
                Type menu = typeof(ModLoader).Assembly.GetType("MSCLoader.ModMenu", false);
                MethodInfo save = menu != null ? menu.GetMethod("SaveSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Mod) }, null) : null;
                if (save == null || MoprMod.Instance == null) return false;
                save.Invoke(null, new object[] { MoprMod.Instance });
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Report("settings/Save", ex);
                return false;
            }
        }

        #region Language
        static Language SelectedLanguage() => Loc.FromIndex(languageSelect != null ? languageSelect.GetSelectedItemIndex() : 0);

        static void OnLanguageSelected() => ApplyLanguage(SelectedLanguage());

        static void ApplyLanguage(Language language)
        {
            if (!Loc.SetLanguage(language)) return;
            LocalizedSettings.Refresh();
            ModConsole.Print("MOPR6: " + Loc.F("con.language", Loc.LanguageNames[(int)language]));
        }

        /// <summary>A dropdown index from MOPR6's settings.json, or -1 (first start, unreadable).</summary>
        static int SavedDropDown(string id)
        {
            try
            {
                if (MoprMod.Instance == null) return -1;
                string path = Path.Combine(ModLoader.GetModSettingsFolder(MoprMod.Instance), "settings.json");
                if (!File.Exists(path)) return -1;
                int value;
                return SettingsFile.TryInt(SettingsFile.Read(path), id, out value) ? value : -1;
            }
            catch (Exception ex)
            {
                ModLog.Report("settings/ReadLanguage", ex);
                return -1;
            }
        }
        #endregion

        #region Builders (each registers its text for live relabeling)
        /// <summary>A header, its hint and one checkbox (with its hint) per system of the tier.</summary>
        static void Section(string header, string hint, Tier tier)
        {
            Header(header);
            Hint(hint);
            foreach (Optimization s in SystemHost.Systems)
            {
                if (s.Tier != tier) continue;
                string key = "menu.sys." + s.Id;
                systemBoxes[s.Id] = CheckBox("sys." + s.Id, key, s.DefaultOn, Apply);
                Hint(key + ".hint");
            }
        }

        static void Header(string key, Color background)
        {
            SettingsHeader header = Settings.AddHeader(Loc.T(key), background, HeaderTitle);
            LocalizedSettings.Header(header, () => Loc.T(key));
        }

        static void Header(string key)
        {
            SettingsHeader header = Settings.AddHeader(Loc.T(key));
            LocalizedSettings.Header(header, () => Loc.T(key));
        }

        static void Hint(string key) => Text(() => Colorize(HintHex, Loc.T(key)));

        static void Text(Func<string> producer)
        {
            SettingsText text = Settings.AddText(producer());
            LocalizedSettings.Text(text, producer);
        }

        static void Button(string key, Action onClick)
        {
            SettingsButton button = Settings.AddButton(Loc.T(key), onClick);
            LocalizedSettings.Button(button, () => Loc.T(key));
        }

        static SettingsCheckBox CheckBox(string id, string key, bool value, Action onChange = null)
        {
            SettingsCheckBox box = Settings.AddCheckBox(id, Loc.T(key), value, onChange);
            LocalizedSettings.Label(box, () => Loc.T(key));
            return box;
        }

        static string Colorize(string hex, string text) => "<color=" + hex + ">" + text + "</color>";
        #endregion

        #region Actions
        static void OnReportButton()
        {
            string path;
            string err = BugReport.Write(out path);
            if (err != null)
            {
                ModUI.ShowMessage(Loc.F("con.report_failed", err), Loc.T("msg.title"));
                return;
            }
            ModUI.ShowCustomMessage(Loc.F("msg.report_saved", BugReport.Shown(path)), Loc.T("msg.title"),
                new MsgBoxBtn[]
                {
                    ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.open_report"), () => OpenUrl(path)),
                    ModUI.CreateMessageBoxBtn(Loc.T("common.ok")),
                },
                new MsgBoxBtn[] { });
        }

        internal static void OpenUrl(string target)
        {
            try { System.Diagnostics.Process.Start(target); }
            catch (Exception ex) { ModLog.Report("settings/Open", ex, target); }
        }
        #endregion
    }
}
