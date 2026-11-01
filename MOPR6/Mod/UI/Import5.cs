// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// "mopr import5" and the settings button (requirement 1.4, design §1): reads MOPR 5.x's MSCLoader settings
// (Mods\Config\Mod Settings\MOPR\settings.json, never written), takes over what MOPR6 already has and saves MOPR6's
// page. 5.x IDs used:
//   enginePlayMakerFastPath -> sys.engine.fsm, sys.engine.property, sys.engine.writes, sys.engine.ik
//                              (one switch for the whole Engine Lab in 5.x)
//   engineSharedMouseRay    -> sys.engine.mouseray (5.x ran it only with the Engine Lab on)
//   enginePlayMakerVerify   -> verify
//   checkUpdates            -> checkUpdates
//   language                -> language (same order: English, Russian, Polish)
// Everything else of 5.x (modes, render, world, experimental) has no counterpart in this stage and is not imported.

using System;
using System.Collections.Generic;
using System.IO;
using MOPR6.Diagnostics;
using MOPR6.Localization;
using MSCLoader;

namespace MOPR6.UI
{
    internal static class Import5
    {
        static readonly string[] EngineLab = { "sys.engine.fsm", "sys.engine.property", "sys.engine.writes", "sys.engine.ik" };

        /// <summary>Imports and returns the answer in <paramref name="language"/>.</summary>
        internal static string Run(Language language)
        {
            string path = SettingsFile5x();
            if (path == null || !File.Exists(path)) return Loc.FIn(language, "con.import.no_file", BugReport.Shown(path ?? "Mod Settings/MOPR"));
            Dictionary<string, object> values;
            try
            {
                values = SettingsFile.Read(path);
            }
            catch (Exception ex)
            {
                ModLog.Report("import5/Read", ex, path);
                return Loc.FIn(language, "con.import.failed", ex.Message);
            }

            List<string> done = new List<string>();
            bool engine = true;
            if (SettingsFile.TryBool(values, "enginePlayMakerFastPath", out bool lab))
            {
                engine = lab;
                foreach (string id in EngineLab) SettingsPage.SetCheckBox(id, lab);
                done.Add("enginePlayMakerFastPath=" + lab);
            }
            if (SettingsFile.TryBool(values, "engineSharedMouseRay", out bool ray))
            {
                SettingsPage.SetCheckBox("sys.engine.mouseray", ray && engine);
                done.Add("engineSharedMouseRay=" + ray);
            }
            if (SettingsFile.TryBool(values, "enginePlayMakerVerify", out bool verify))
            {
                SettingsPage.SetCheckBox("verify", verify);
                done.Add("enginePlayMakerVerify=" + verify);
            }
            if (SettingsFile.TryBool(values, "checkUpdates", out bool updates))
            {
                SettingsPage.SetCheckBox("checkUpdates", updates);
                done.Add("checkUpdates=" + updates);
            }
            if (SettingsFile.TryInt(values, "language", out int lang) && SettingsPage.SetLanguageIndex(lang))
                done.Add("language=" + Loc.LanguageNames[lang]);

            if (done.Count == 0) return Loc.FIn(language, "con.import.nothing");
            SettingsPage.Apply();
            string answer = Loc.FIn(language, "con.import.done", string.Join(", ", done.ToArray()));
            ModLog.Line("import5: " + string.Join(", ", done.ToArray()));
            return SettingsPage.Save() ? answer : answer + "\n" + Loc.FIn(language, "con.import.not_saved");
        }

        /// <summary>Mods\Config\Mod Settings\MOPR\settings.json next to MOPR6's own folder.</summary>
        static string SettingsFile5x()
        {
            if (MoprMod.Instance == null) return null;
            string own = ModLoader.GetModSettingsFolder(MoprMod.Instance).TrimEnd('\\', '/');
            string parent = Path.GetDirectoryName(own);
            return parent == null ? null : Path.Combine(Path.Combine(parent, MoprMod.LegacyModId), "settings.json");
        }
    }
}
