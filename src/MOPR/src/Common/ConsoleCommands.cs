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

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using MSCLoader;

using MOP.Rules;
using MOP.Vehicles.Cases;

namespace MOP.Common
{
    class ConsoleCommands : ConsoleCommand
    {
        // MOP - REVIVAL 5.1: "mopr" (the mod ID); "mop" still works for old guides and habits.
        public override string Name => "mopr";
        public override string Alias => "mop";
        public override string Help => "Use \"mopr help\" to see command list!";
        public override bool ShowInHelp => true;

        const string HelpList = "<color=yellow>help</color> - Show this list\n" +
                                "<color=yellow>version</color> - Prints MOP version\n" +
                                "<color=yellow>rules [all]</color> - Rule files: active, not used and why, problems (all: every built-in file)\n" +
                                "<color=yellow>rules why [object name]</color> - Every rule that names this object\n" +
                                "<color=yellow>rules export [ModID]</color> - Writes MOPR's built-in rules of a mod into the rules folder, to edit them\n" +
                                "<color=yellow>rules convert [file]</color> - Converts an old .mopconfig or Custom.txt file to JSON (the old one is kept as .bak)\n" +
                                "<color=yellow>reload [all]</color> - Reads the rule files again. \"all\": also the files of mods that are not installed\n" +
                                "<color=yellow>new [name]</color> - Creates your own rule file (Rules/name.json) with explanations and opens it\n" +
                                "<color=yellow>open [name]</color> - Opens a rule file (without a name: the rules folder)\n" +
                                "<color=yellow>open-config</color> - Opens MOP config folder\n" +
                                "<color=yellow>delete [name]</color> - Deletes one of your rule files\n" +
                                "<color=yellow>cat [name or ModID]</color> - Prints a rule file (yours, an old one, or the built-in one of a mod)\n" +
                                "<color=yellow>generate-list [true/false]</color> - Generates text files which contain the list of items that are toggled by MOP\n" +
                                "<color=yellow>load-rules [true/false]</color> - If set to false, no rule applies from the next load (for testing)\n" +
                                "<color=yellow>force-crash [critical]</color> - Forces MOP to crash itself. If 'critical' is added, it will force a critical crash.\n" +
                                "<color=yellow>resolution</color> - Lists the available resolutions\n" +
                                "<color=yellow>resolution [nOfResolution]</color> - Sets the desired resolution from 'mopr resolution' list\n" +
                                "<color=yellow>resolution [width] [height]</color> - Sets the desired resolution\n" +
                                "<color=yellow>force-load-restart [true/false]</color> - Forces the 'missing Satsuma parts' game reload to happen\n" +
                                "<color=yellow>stop</color> - Stops MOP. MIGHT BREAK THE GAME!\n" +
                                "<color=yellow>start</color> - Starts MOP back again\n" +
                                "<color=yellow>quality [0-5]</color> - Set quality setting\n" +
                                "<color=yellow>debug_montior</color> - Shows/hides the MOP debug monitor\n" +
                                "<color=yellow>presave</color> - Runs pre-save script\n" +
                                "<color=yellow>render</color> - Shows the render optimizer report (layers, shadow LOD, lamps)\n" +
                                "<color=yellow>render rescan</color> - Re-analyses all renderers and lamps\n" +
                                "<color=yellow>render restore</color> - Restores every visual value changed by the render optimizer\n" +
                                "<color=yellow>audit</color> - Scans managed objects for PlayMaker FSMs that may reset when re-enabled\n" +
                                "<color=yellow>guard</color> - Writes the list of FSMs protected by the restart guard\n" +
                                "<color=yellow>engine [on|off|shadow|reset|detail]</color> - PlayMaker fast path: switch mode or show statistics";

        public override void Run(string[] args)
        {
            if (args.Length == 0)
            {
                ModConsole.Log("See \"mopr help\" for command list.");
                return;
            }

            switch (args[0].ToUpperInvariant())
            {
                default:
                    ModConsole.Log("Invalid command. Type \"mopr help\" for command list.");
                    break;
                case "RENDER":
                    if (Next.RenderOptimizer.Instance == null)
                    {
                        ModConsole.Log("The render optimizer only runs in game.");
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
                        ModConsole.Log("[MOPR] Render optimizer suspended and every value restored. Change any render setting to re-apply.");
                    }
                    ModConsole.Log(Next.RenderOptimizer.Instance.GetReport());
                    ModConsole.Log(Next.VehicleMeshMerge.Report());
                    break;
                case "AUDIT":
                    if (ModLoader.CurrentScene != CurrentScene.Game || Hypervisor.Instance == null)
                    {
                        ModConsole.Log("The audit can only run in game.");
                        return;
                    }
                    Next.FsmAudit.Run();
                    break;
                case "ENGINE":
                    Next.Engine.EngineLab.Command(args);
                    break;
                case "GUARD":
                    ModConsole.Log($"[MOPR] Restart guard: {(Next.RestartGuard.Enabled ? "enabled" : "disabled")}, " +
                                   $"{Next.RestartGuard.GuardedCount} FSMs protected. Report: {Next.RestartGuard.WriteReport()}");
                    break;
                case "?":
                case "H":
                case "HELP":
                    string[] helpList = HelpList.Split('\n');
                    Array.Sort(helpList, StringComparer.InvariantCulture);
                    if (args.Length > 1)
                    {
                        bool commandsFound = false;
                        foreach (string s in helpList)
                        {
                            if (s.Split('-')[0].Contains(args[1]))
                            {
                                ModConsole.Log(s);
                                commandsFound = true;
                            }
                        }
                        if (!commandsFound)
                        {
                            ModConsole.Log($"Command {args[1]} not found.");
                        }
                        return;
                    }
                    ModConsole.Log(string.Join("\n", helpList));
                    break;
                case "RULES":
                    if (args.Length > 1 && args[1] == "roll")
                    {
                        ModConsole.Log("\n<color=yellow>You know the rules and so do I\n" +
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
                            if (rest.Length == 0) { ModConsole.Log("Usage: mopr rules why <object name>"); return; }
                            ModConsole.Log(RulesManager.Why(rest));
                            break;
                        case "EXPORT":
                            if (rest.Length == 0) { ModConsole.Log("Usage: mopr rules export <ModID>"); return; }
                            ModConsole.Log("[MOPR] " + RulesManager.Export(rest));
                            break;
                        case "CONVERT":
                            if (rest.Length == 0) { ModConsole.Log("Usage: mopr rules convert <ModID or Custom>"); return; }
                            ModConsole.Log("[MOPR] " + RulesManager.Convert(rest));
                            break;
                        default:
                            ModConsole.Log(RulesManager.Report(sub == "ALL"));
                            break;
                    }
                    break;
                case "RELOAD":
                    RulesManager.ActivateAll = args.Length > 1 && args[1].ToUpperInvariant() == "ALL";
                    // In the menu the result applies at once; during a game it applies at the next load.
                    RulesManager.Load(ModLoader.CurrentScene != CurrentScene.Game);
                    break;
                case "NEW":
                    ModConsole.Log("[MOPR] " + RulesManager.NewFile(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null));
                    break;
                case "VERSION":
                    ModConsole.Log($"{MOP.ModVersion} for {MOP.Edition}");
                    break;
                case "COWSAY":
                    string say = string.Join(" ", args, 1, args.Length - 1);

                    ModConsole.Log($"< {say} >\n" +
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
                            ModConsole.Log($"No rule file \"{name}\". Your files are in {RulesManager.Folder}");
                            return;
                        }
                        Process.Start(file);
                    }
                    break;
                case "DELETE":
                    {
                        if (args.Length == 1)
                        {
                            ModConsole.Log("Usage: mopr delete <name>");
                            return;
                        }
                        string name = string.Join(" ", args, 1, args.Length - 1);
                        string file = RulesManager.FindFile(name);
                        if (file == null)
                        {
                            ModConsole.Log($"No rule file \"{name}\". Your files are in {RulesManager.Folder}");
                            return;
                        }
                        File.Delete(file);
                        ModConsole.Log($"[MOPR] Deleted {file}. It applies at the next game load.");
                    }
                    break;
                case "CAT":
                    {
                        if (args.Length == 1)
                        {
                            ModConsole.Log("Usage: mopr cat <name or ModID>");
                            return;
                        }
                        string name = string.Join(" ", args, 1, args.Length - 1);
                        string text = RulesManager.FileText(name);
                        ModConsole.Log(text ?? $"No rule file \"{name}\" and no built-in rules for a mod with that ID.");
                    }
                    break;
                case "GENERATE-LIST":
                    if (args.Length > 1)
                    {
                        if (RulesManager.LoadRules && RulesManager.Book.Active.Count > 0)
                        {
                            ModConsole.Log("<color=red>WARNING:</color> For accurate results, use \"mopr load-rules false\" to prevent MOP from using rule files.");
                        }

                        MopSettings.GenerateToggledItemsListDebug = args[1].ToUpperInvariant() == "TRUE";
                    }

                    ModConsole.Log($"Generating toggled elements list is set to " +
                                   $"<color={(MopSettings.GenerateToggledItemsListDebug ? "green" : "red")}>{MopSettings.GenerateToggledItemsListDebug}</color>");
                    break;
                case "LOAD-RULES":
                    if (args.Length > 1)
                    {
                        RulesManager.LoadRules = args[1].ToUpperInvariant() == "TRUE";
                        if (!RulesManager.LoadRules)
                        {
                            ModConsole.Log("\n\n<color=red>WARNING:</color>\nDisabling rule files may cause serious issues with the game, or even break your save file.\n\n" +
                                             "<b><color=red>!!! USE ONLY AT YOUR OWN RISK !!!</color></b>\n\n");
                        }
                        RulesManager.Load(ModLoader.CurrentScene != CurrentScene.Game);
                    }

                    ModConsole.Log($"Loading rule files is set to " +
                                     $"<color={(RulesManager.LoadRules ? "green" : "red")}>{RulesManager.LoadRules}</color>");
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
                                if (res.width == Screen.width && res.height == Screen.height)
                                {
                                    availableResolutions += $"  <color=green>{i + 1}. {res.width}x{res.height}</color>\n";
                                }
                                else
                                {
                                    availableResolutions += $"  {i + 1}. {res.width}x{res.height}\n";
                                }
                            }
                            ModConsole.Log(availableResolutions);
                            ModConsole.Log("Use '<color=yellow>mopr resolution [nOfResolution]</color>' to set the preferred resolution.");
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
                        ModConsole.LogError("Failed setting resolution.");
                    }
                    break;
                case "QUALITY":
                    try
                    {
                        QualitySettings.SetQualityLevel(int.Parse(args[1]), true);
                    }
                    catch
                    {
                        ModConsole.LogError("Failed setting quality settings.");
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
                            ModConsole.LogError("Unrecognized argument: " + var);
                            return;
                        }
                    }

                    ModConsole.Log($"force-load-restart is set to <color=yellow>{MopSettings.ForceLoadRestart}</color>.");
                    break;
                case "STOP":
                    if (ModLoader.CurrentScene != CurrentScene.Game)
                    {
                        ModConsole.Log("[MOPR] MOP can only be stopped while in-game.");
                        return;
                    }

                    if (!MopSettings.IsModActive)
                    {
                        ModConsole.Log("[MOPR] MOP is not running.");
                        return;
                    }

                    Hypervisor.Instance.StopAllCoroutines();
                    Hypervisor.Instance.ToggleAll(true);
                    MopSettings.IsModActive = false; 
                    ModConsole.Log("[MOPR] MOP has been stopped. Some things may be broken. Saving game in this state may break your game!");
                    break;
                case "START":
                    if (ModLoader.CurrentScene != CurrentScene.Game)
                    {
                        ModConsole.Log("[MOPR] MOP can only be started while in-game.");
                        return;
                    }

                    if (MopSettings.IsModActive)
                    {
                        ModConsole.Log("[MOPR] MOP is already running.");
                        return;
                    }

                    Hypervisor.Instance.ToggleAll(false, Enumerations.ToggleAllMode.OnLoad);
                    Hypervisor.Instance.Startup();
                    break;
                case "DB":
                case "DEBUG_MONITOR":
                    if (ModLoader.CurrentScene != CurrentScene.Game)
                    {
                        ModConsole.Log("[MOPR] Debug monitor can only be used in-game.");
                        return;
                    }

                    Hypervisor.Instance.ToggleDebugMode();
                    break;
                case "PRESAVE":
                    if (ModLoader.CurrentScene != CurrentScene.Game)
                    {
                        ModConsole.Log("[MOPR] Can only be used in-game.");
                        return;
                    }
                    ModConsole.Log("[MOPR] Running pre-save...");
                    Hypervisor.Instance?.PreSaveGame();
                    break;
                case "YEET":
                    // yeet.
                    if (ModLoader.CurrentScene != CurrentScene.Game)
                    {
                        ModConsole.Log("[MOPR] Can only be used in-game.");
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
