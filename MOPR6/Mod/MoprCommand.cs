// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Console command "mopr" (design §13): status, on | off (main switch), on | off | verify ID | all (one system,
// for this session), verify (mismatches), restore, report, import5, catalog, update; diagnostics errors, ledger,
// barrier, lang; the harness's probe and the older "sys <id>|all on|verify|off". The answer is in the player's language
// (requirement 13.1); the TestBot gets the same command in English (TestApi.Command). Technical lines (system states and
// counters, paths) are not translated.

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Diagnostics;
using MOPR6.Host;
using MOPR6.Ledger;
using MOPR6.Localization;
using MOPR6.Systems;
using MOPR6.Testing;
using MOPR6.UI;
using MSCLoader;

namespace MOPR6
{
    public sealed class MoprCommand : ConsoleCommand
    {
        public override string Name => "mopr";
        public override string Help => "MOPR6: mopr help";
        public override bool ShowInHelp => true;

        public override void Run(string[] args)
        {
            string result;
            try { result = Execute(args, Loc.Current); }
            catch (Exception ex)
            {
                ModLog.Report("console/" + (args.Length > 0 ? args[0] : ""), ex);
                result = "error: " + ex.Message;
            }
            if (!string.IsNullOrEmpty(result)) ModConsole.Print(result.TrimEnd('\n'));
        }

        internal static string Execute(string[] args, Language lang)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            string arg = args.Length > 1 ? args[1] : null;
            switch (cmd)
            {
                case "help":
                    return Loc.FIn(lang, "con.help");
                case "status":
                    return "MOPR6 " + BuildInfo.Version + (MoprMod.Conflict5x ? " (" + Loc.FIn(lang, "con.inactive") + ")" : "") + "\n" +
                           Loc.FIn(lang, "con.main_state", Loc.FIn(lang, SettingsPage.MainOn ? "con.word.on" : "con.word.off")) + "\n" +
                           SystemHost.Status() + Loc.FIn(lang, "con.ledger_line", TestApi.LedgerCount(), TestApi.Barrier());
                case "on":
                case "off":
                    if (MoprMod.Conflict5x) return Loc.FIn(lang, "con.inactive");
                    if (arg == null) return SettingsPage.SetMain(cmd == "on", lang);
                    return SystemCommand(arg, cmd, lang);
                case "verify":
                    if (arg == null) return Loc.FIn(lang, "con.mismatches", SystemHost.Mismatches());
                    return SystemCommand(arg, "verify", lang);
                case "sys":
                    // The harness's older form: mopr sys <id>|all on|verify|off
                    if (args.Length < 3) return "mopr sys ID|all on|verify|off\n" + SystemHost.Status();
                    return SystemCommand(args[1], args[2].ToLowerInvariant(), lang);
                case "restore":
                    {
                        int n = TestApi.LedgerCount();
                        List<Core.Ledger.RestoreRecord> odd = Arbiters.RestoreAll("console");
                        return Loc.FIn(lang, "con.restored", n - odd.Count, odd.Count);
                    }
                case "report":
                    {
                        string err = BugReport.Write(out string path);
                        return err == null ? Loc.FIn(lang, "con.report_saved", BugReport.Shown(path)) : Loc.FIn(lang, "con.report_failed", err);
                    }
                case "import5":
                    return Import5.Run(lang);
                case "catalog":
                    Catalog.Vanilla.Load();
                    return Catalog.Vanilla.GameLine() + "\n" + Catalog.Vanilla.Status();
                case "update":
                    return UpdateChecker.CheckNow(MoprMod.MenuHost, lang);
                case "errors":
                    string e = TestApi.Errors();
                    return e.Length == 0 ? Loc.FIn(lang, "con.no_errors") : e;
                case "barrier":
                    return TestApi.Barrier();
                case "ledger":
                    string d = Arbiters.Dump();
                    return d.Length == 0 ? Loc.FIn(lang, "con.ledger_empty") : d;
                case "lang":
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (Language l in new[] { Language.Russian, Language.Polish })
                        {
                            List<string> missing = Loc.MissingKeys(l);
                            sb.Append(Loc.FIn(lang, "con.lang_check", Loc.LanguageNames[(int)l], missing.Count,
                                              missing.Count > 0 ? " (" + string.Join(", ", missing.ToArray()) + ")" : "")).Append('\n');
                        }
                        return sb.ToString();
                    }
                case "probe":
                    return Probe(args);
                case "world":
                    // Test only (TestBot, picture check up close): "world distance <m>" sets the switch-off distance of
                    // world.deactivate for this session, "world distance 0" back to the defaults.
                    if (args.Length == 3 && args[1] == "distance" &&
                        float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m))
                        return World.Dormancy.Instance.SetTestDistance(m);
                    return "mopr world distance <metres> (0 = defaults)";
                default:
                    return Loc.FIn(lang, "con.unknown", cmd, Loc.FIn(lang, "con.help"));
            }
        }

        /// <summary>on | off | verify one system or all exact ones, for this session.</summary>
        static string SystemCommand(string id, string mode, Language lang)
        {
            if (MoprMod.Conflict5x) return Loc.FIn(lang, "con.inactive");
            if (!SystemHost.Ready) return Loc.FIn(lang, "con.not_ready");
            if (mode != "off" && !SettingsPage.MainOn) return Loc.FIn(lang, "con.main_is_off");
            List<Optimization> targets = new List<Optimization>();
            if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Optimization s in SystemHost.Systems) if (s.Tier == Tier.Exact) targets.Add(s);
            }
            else if (string.Equals(id, "render", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == "verify") mode = "on";   // render systems have no verify mode
                foreach (Optimization s in SystemHost.Systems) if (s.Tier == Tier.Render) targets.Add(s);
            }
            else
            {
                Optimization s = SystemHost.Find(id);
                if (s == null)
                {
                    List<string> ids = new List<string>();
                    foreach (Optimization x in SystemHost.Systems) ids.Add(x.Id);
                    return Loc.FIn(lang, "con.no_system", id, string.Join(", ", ids.ToArray()));
                }
                targets.Add(s);
            }
            StringBuilder sb = new StringBuilder();
            foreach (Optimization s in targets)
            {
                string err = TestApi.SetMode(s.Id, mode);
                sb.Append(err != null ? s.Id + ": " + err : TestApi.GetMode(s.Id)).Append('\n');
            }
            return sb.Append(Loc.FIn(lang, "con.session_only")).ToString();
        }

        static string Probe(string[] args)
        {
            ProbeSystem p = ProbeSystem.Instance;
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "hide":
                    if (args.Length < 3) return "probe hide PATH";
                    p.HidePath = string.Join(" ", args, 2, args.Length - 2);
                    p.ThrowOnTick = false;
                    return SystemHost.Install(p, SystemMode.On) ?? "probe hides the renderers under " + p.HidePath;
                case "throw":
                    p.ThrowOnTick = true;
                    return SystemHost.Install(p, SystemMode.On) ?? "probe throws from its tick";
                case "off":
                    SystemHost.Remove(p);
                    p.ThrowOnTick = false;
                    return "probe removed";
                default:
                    StringBuilder sb = new StringBuilder();
                    p.Report(sb);
                    return p.State + "\n" + sb;
            }
        }
    }
}
