// Modern Optimization Plugin
// Copyright(C) 2019-2022 Athlon

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.
//
// MOP - REVIVAL 5.1: the messages follow the mod language (keys "con.cmd.*" and "con.help.*" in
// Localization/Console.*.cs). The commands themselves and their arguments are not translated.

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using UnityEngine;
using MSCLoader;

using MOP.Localization;
using MOP.Rules;
using MOP.Vehicles.Cases;

namespace MOP.Common
{
    class ConsoleCommands : ConsoleCommand
    {
        // MOP - REVIVAL 5.1: "mopr" (the mod ID). The old "mop" is gone, so it can't clash with the original MOP.
        public override string Name => "mopr";
        public override string Help => Loc.T("con.cmd.help");
        public override bool ShowInHelp => true;

        const int DefaultLogLines = 40;

        /// <summary>"mopr help": the command (not translated) and the key of its description.</summary>
        static readonly string[,] HelpList =
        {
            { "help", "con.help.help" },
            { "version", "con.help.version" },
            { "log [n]", "con.help.log" },
            { "rules [all]", "con.help.rules" },
            { "rules why [object name]", "con.help.rules_why" },
            { "rules export [ModID]", "con.help.rules_export" },
            { "rules convert [file]", "con.help.rules_convert" },
            { "reload [all]", "con.help.reload" },
            { "new [name]", "con.help.new" },
            { "open [name]", "con.help.open" },
            { "open-config", "con.help.open_config" },
            { "delete [name]", "con.help.delete" },
            { "cat [name or ModID]", "con.help.cat" },
            { "generate-list [true/false]", "con.help.generate_list" },
            { "load-rules [true/false]", "con.help.load_rules" },
            { "force-crash [critical]", "con.help.force_crash" },
            { "resolution", "con.help.resolution" },
            { "resolution [n]", "con.help.resolution_n" },
            { "resolution [width] [height]", "con.help.resolution_wh" },
            { "force-load-restart [true/false]", "con.help.force_load_restart" },
            { "stop", "con.help.stop" },
            { "start", "con.help.start" },
            { "quality [0-5]", "con.help.quality" },
            { "debug_monitor", "con.help.debug_monitor" },
            { "presave", "con.help.presave" },
            { "render", "con.help.render" },
            { "render rescan", "con.help.render_rescan" },
            { "render restore", "con.help.render_restore" },
            { "audit", "con.help.audit" },
            { "guard", "con.help.guard" },
            { "engine [on|off|shadow|reset|detail]", "con.help.engine" },
            { "update", "con.help.update" },
        };

        /// <summary>Prints without storing: answers to a command the player typed are not part of the mod log.</summary>
        static void Print(string text) => MSCLoader.ModConsole.Log(text);

        static void PrintHelp(string filter)
        {
            List<int> rows = new List<int>();
            for (int i = 0; i < HelpList.GetLength(0); i++)
                if (filter == null || HelpList[i, 0].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    rows.Add(i);
            if (rows.Count == 0) { ModConsole.Info("con.cmd.not_found", filter); return; }

            // Alphabetical by the command ("render" before "render rescan").
            rows.Sort((a, b) => string.CompareOrdinal(HelpList[a, 0], HelpList[b, 0]));
            List<string> lines = new List<string>();
            foreach (int i in rows) lines.Add(MoprColors.Warning(HelpList[i, 0]) + " - " + Loc.T(HelpList[i, 1]));
            Print(string.Join("\n", lines.ToArray()));
        }

        static bool InGame => ModLoader.CurrentScene == CurrentScene.Game;

        public override void Run(string[] args)
        {
            if (args.Length == 0)
            {
                ModConsole.Info("con.cmd.see_help");
                return;
            }

            switch (args[0].ToUpperInvariant())
            {
                default:
                    ModConsole.Info("con.cmd.invalid");
                    break;
                case "RENDER":
                    if (Next.RenderOptimizer.Instance == null)
                    {
                        ModConsole.Info("con.cmd.render_only_in_game");
                        return;
                    }
                    if (args.Length > 1 && args[1].ToUpperInvariant() == "RESCAN")
                    {
                        Next.RenderOptimizer.Instance.Rescan();
                        Next.RenderOptimizer.Instance.ApplySettings();
                    }
                    else if (args.Length > 1 && args[1].ToUpperInvariant() == "RESTORE")
                    {
                        Next.RenderOptimizer.Instance.Suspend();
                        ModConsole.Info("con.cmd.render_restored");
                    }
                    // Diagnostic dumps: English, like the statistics in bug reports.
                    ModConsole.Log(Next.RenderOptimizer.Instance.GetReport());
                    ModConsole.Log(Next.VehicleMeshMerge.Report());
                    break;
                case "AUDIT":
                    if (!InGame || Hypervisor.Instance == null)
                    {
                        ModConsole.Info("con.cmd.audit_only_in_game");
                        return;
                    }
                    Next.FsmAudit.Run();
                    break;
                case "ENGINE":
                    Next.Engine.EngineLab.Command(args);
                    break;
                case "UPDATE":
                    // "mopr update preview" (not in the help): also shows the message for the latest release, to
                    // see how it looks.
                    UpdateChecker.CheckNow(MenuHelper.Instance, args.Length > 1 && args[1].ToUpperInvariant() == "PREVIEW");
                    break;
                case "GUARD":
                    ModConsole.Info("con.cmd.guard", Loc.OnOff(Next.RestartGuard.Enabled), Next.RestartGuard.GuardedCount,
                                    Next.RestartGuard.WriteReport());
                    break;
                case "LOG":
                    {
                        int count = DefaultLogLines;
                        if (args.Length > 1 && (!int.TryParse(args[1], out count) || count < 1))
                        {
                            ModConsole.Info("con.cmd.usage", "mopr log [n]");
                            return;
                        }
                        Print(MoprColors.StyledTag + " " + MoprColors.Colorize(MoprColors.ConsoleBody, Loc.F("con.cmd.log_head", count)) +
                              "\n" + ModConsole.Tail(count));
                    }
                    break;
                case "?":
                case "H":
                case "HELP":
                    PrintHelp(args.Length > 1 ? args[1] : null);
                    break;
                case "RULES":
                    if (args.Length > 1 && args[1] == "roll")
                    {
                        Print("\n<color=yellow>You know the rules and so do I\n" +
                              "A full commitment's what I'm thinking of\n" +
                              "You wouldn't get this from any other guy\n" +
                              "I just wanna tell you how I'm feeling\n" +
                              "Gotta make you understand\n" +
                              "Never gonna give you up\n" +
                              "Never gonna let you down\n" +
                              "Never gonna run around and desert you\n" +
                              "Never gonna make you cry\n" +
                              "Never gonna say goodbye\n" +
                              "Never gonna tell a lie and hurt you</color>\n\n");
                        return;
                    }

                    string sub = args.Length > 1 ? args[1].ToUpperInvariant() : "";
                    string rest = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";
                    switch (sub)
                    {
                        case "WHY":
                            if (rest.Length == 0) { ModConsole.Info("con.cmd.usage", "mopr rules why [object name]"); return; }
                            Print(RulesManager.Why(rest));
                            break;
                        case "EXPORT":
                            if (rest.Length == 0) { ModConsole.Info("con.cmd.usage", "mopr rules export [ModID]"); return; }
                            RulesManager.Export(rest);
                            break;
                        case "CONVERT":
                            if (rest.Length == 0) { ModConsole.Info("con.cmd.usage", "mopr rules convert [ModID or Custom]"); return; }
                            RulesManager.Convert(rest);
                            break;
                        default:
                            // In the console language; bug reports get their own English copy (RulesManager.ReportForBugReport).
                            Print(RulesManager.Report(sub == "ALL"));
                            break;
                    }
                    break;
                case "RELOAD":
                    RulesManager.ActivateAll = args.Length > 1 && args[1].ToUpperInvariant() == "ALL";
                    // In the menu the result applies at once; during a game it applies at the next load.
                    RulesManager.Load(!InGame);
                    break;
                case "NEW":
                    RulesManager.NewFile(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null);
                    break;
                case "VERSION":
                    ModConsole.Info("con.cmd.version", MoprColors.Version("v" + MOP.ModVersion.Replace("_", " ")), MOP.Edition);
                    break;
                case "COWSAY":
                    string say = string.Join(" ", args, 1, args.Length - 1);

                    Print($"< {say} >\n" +
                          "        \\   ^__^\n" +
                          "         \\  (oo)\\____\n" +
                          "            (__)\\          )\\/\\\n" +
                          "                ||  ----w  |\n" +
                          "                ||           || ");
                    break;
                case "OPEN-CONFIG":
                    Process.Start(MOP.ModConfigPath);
                    break;
                case "OPEN":
                    {
                        if (args.Length == 1)
                        {
                            RulesManager.OpenFolder();
                            return;
                        }
                        string name = string.Join(" ", args, 1, args.Length - 1);
                        string file = RulesManager.FindFile(name);
                        if (file == null)
                        {
                            ModConsole.Info("con.cmd.no_file", name, RulesManager.Folder);
                            return;
                        }
                        Process.Start(file);
                    }
                    break;
                case "DELETE":
                    {
                        if (args.Length == 1)
                        {
                            ModConsole.Info("con.cmd.usage", "mopr delete [name]");
                            return;
                        }
                        string name = string.Join(" ", args, 1, args.Length - 1);
                        string file = RulesManager.FindFile(name);
                        if (file == null)
                        {
                            ModConsole.Info("con.cmd.no_file", name, RulesManager.Folder);
                            return;
                        }
                        File.Delete(file);
                        ModConsole.Info("con.cmd.deleted", file);
                    }
                    break;
                case "CAT":
                    {
                        if (args.Length == 1)
                        {
                            ModConsole.Info("con.cmd.usage", "mopr cat [name or ModID]");
                            return;
                        }
                        string name = string.Join(" ", args, 1, args.Length - 1);
                        string text = RulesManager.FileText(name);
                        if (text != null) Print(text);
                        else ModConsole.Info("con.cmd.cat_none", name);
                    }
                    break;
                case "GENERATE-LIST":
                    if (args.Length > 1)
                    {
                        if (RulesManager.LoadRules && RulesManager.Book.Active.Count > 0)
                        {
                            ModConsole.Warn("con.cmd.genlist_warning");
                        }

                        MopSettings.GenerateToggledItemsListDebug = args[1].ToUpperInvariant() == "TRUE";
                    }

                    ModConsole.Info("con.cmd.genlist_state", Loc.OnOff(MopSettings.GenerateToggledItemsListDebug));
                    break;
                case "LOAD-RULES":
                    if (args.Length > 1)
                    {
                        RulesManager.LoadRules = args[1].ToUpperInvariant() == "TRUE";
                        if (!RulesManager.LoadRules)
                        {
                            ModConsole.Warn("con.cmd.load_rules_warning");
                        }
                        RulesManager.Load(!InGame);
                    }

                    ModConsole.Info("con.cmd.load_rules_state", Loc.OnOff(RulesManager.LoadRules));
                    break;
                case "FORCE-CRASH":
                    bool isCritical = false;
                    if (args.Length > 1 && args[1].ToUpperInvariant() == "CRITICAL")
                    {
                        isCritical = true;
                    }

                    try
                    {
                        throw new Exception("Test Exception");
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, isCritical, "TEST_EXCEPTION");
                    }
                    break;
                case "RESOLUTION":
                    try
                    {
                        if (args.Length == 1)
                        {
                            string availableResolutions = "";
                            for (int i = 0; i < Screen.resolutions.Length; ++i)
                            {
                                Resolution res = Screen.resolutions[i];
                                string entry = $"  {i + 1}. {res.width}x{res.height}";
                                availableResolutions += (res.width == Screen.width && res.height == Screen.height ? MoprColors.Success(entry) : entry) + "\n";
                            }
                            Print(availableResolutions);
                            ModConsole.Info("con.cmd.resolution_hint");
                            return;
                        }

                        if (args.Length == 2)
                        {
                            int nOfResolution = int.Parse(args[1]) - 1;
                            Resolution res = Screen.resolutions[nOfResolution];
                            Screen.SetResolution(res.width, res.height, false);
                            return;
                        }

                        int width = int.Parse(args[1]);
                        int height = int.Parse(args[2]);

                        Screen.SetResolution(width, height, false);
                    }
                    catch
                    {
                        ModConsole.Err("con.cmd.resolution_failed");
                    }
                    break;
                case "QUALITY":
                    try
                    {
                        QualitySettings.SetQualityLevel(int.Parse(args[1]), true);
                    }
                    catch
                    {
                        ModConsole.Err("con.cmd.quality_failed");
                    }
                    break;
                case "FORCE-LOAD-RESTART":
                    if (args.Length == 2)
                    {
                        string var = args[1].ToUpper();
                        if (var == "TRUE")
                        {
                            MopSettings.ForceLoadRestart = true;
                        }
                        else if (var == "FALSE")
                        {
                            MopSettings.ForceLoadRestart = false;
                        }
                        else
                        {
                            ModConsole.Err("con.cmd.bad_argument", var);
                            return;
                        }
                    }

                    ModConsole.Info("con.cmd.force_load_restart", Loc.OnOff(MopSettings.ForceLoadRestart));
                    break;
                case "STOP":
                    if (!InGame)
                    {
                        ModConsole.Info("con.cmd.stop_only_in_game");
                        return;
                    }

                    if (!MopSettings.IsModActive)
                    {
                        ModConsole.Info("con.cmd.not_running");
                        return;
                    }

                    Hypervisor.Instance.StopAllCoroutines();
                    Hypervisor.Instance.ToggleAll(true);
                    MopSettings.IsModActive = false;
                    ModConsole.Warn("con.cmd.stopped");
                    break;
                case "START":
                    if (!InGame)
                    {
                        ModConsole.Info("con.cmd.start_only_in_game");
                        return;
                    }

                    if (MopSettings.IsModActive)
                    {
                        ModConsole.Info("con.cmd.already_running");
                        return;
                    }

                    Hypervisor.Instance.ToggleAll(false, Enumerations.ToggleAllMode.OnLoad);
                    Hypervisor.Instance.Startup();
                    break;
                case "DB":
                case "DEBUG_MONITOR":
                    if (!InGame)
                    {
                        ModConsole.Info("con.cmd.monitor_only_in_game");
                        return;
                    }

                    Hypervisor.Instance.ToggleDebugMode();
                    break;
                case "PRESAVE":
                    if (!InGame)
                    {
                        ModConsole.Info("con.cmd.only_in_game");
                        return;
                    }
                    ModConsole.Info("con.cmd.presave");
                    Hypervisor.Instance?.PreSaveGame();
                    break;
                case "YEET":
                    // yeet.
                    if (!InGame)
                    {
                        ModConsole.Info("con.cmd.only_in_game");
                        return;
                    }

                    float force = 10000;
                    if (args.Length > 1)
                    {
                        force = int.Parse(args[1]);
                    }

                    Satsuma.Instance.gameObject.GetComponent<Rigidbody>().AddForce(Vector3.up * force, ForceMode.Impulse);
                    break;
            }
        }
    }
}
