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

using MSCLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

using MOP.Rules;
using System.Text;
using System.Linq;

namespace MOP.Common
{
    class ExceptionManager
    {
        private static readonly List<string> erorrsContainer = new List<string>();

        public static DateTime SessionTimeStart { get; set; }
        private static string currentLogFile;

        /// <summary>
        /// Creates then new error dump file
        /// </summary>
        /// <param name="ex"></param>
        /// <summary>
        /// MOP - REVIVAL: forget which errors were already reported (called on every game load), so a new
        /// session reports its errors again.
        /// </summary>
        public static void ResetSession()
        {
            erorrsContainer.Clear();
            suppressedRepeats = 0;
        }

        static int suppressedRepeats;
        public static int SuppressedRepeats => suppressedRepeats;

        public static void New(Exception ex, bool isCritical, string message)
        {
            // Don't save errors that already occured. Keyed on code + exception type + message, so a
            // different failure behind the same code is still reported.
            string key = message + "|" + (ex != null ? ex.GetType().Name + "|" + ex.Message : "");
            if (erorrsContainer.Contains(key))
            {
                suppressedRepeats++;
                return;
            }

            bool isNewFile = false;
            // Log file doens't exist? Generate a new one.
            if (string.IsNullOrEmpty(currentLogFile))
            {
                string fileName = $"{Paths.DefaultErrorLogName}_{DateTime.Now:yyyy-MM-dd-HH-mm}";
                if (File.Exists($"{Paths.LogFolder}/{fileName}.txt"))
                {
                    int crashesInFolder = 0;
                    while (File.Exists($"{Paths.LogFolder}/{fileName}_{crashesInFolder}.txt"))
                    {
                        crashesInFolder++;
                    }

                    fileName += $"_{crashesInFolder}"; 
                }
                
                isNewFile = true;
                currentLogFile = fileName + ".txt";
            }

            string logFilePath = Path.Combine(Paths.LogFolder, currentLogFile).Replace("\\", "/");
            string errorInfo = $"({DateTime.Now:HH:mm:ss.fff})\n  Code: {message}\n  Type: {ex.GetType().Name}\n  Message: {ex.Message}{ex.StackTrace}\n  Target Site: {ex.TargetSite}";
            // Add version stamp, to the new log file.
            if (isNewFile)
            {
                errorInfo = $"MOPR {MOP.ModVersion}\n" + errorInfo;
            }

            // Fix (original MOP bug): the de-duplication buffer was never filled, so an error inside
            // the 0.7 s loop was written to disk and the console on every single tick.
            erorrsContainer.Add(key);

            try
            {
                if (!Paths.LogDirectoryExists)
                    Directory.CreateDirectory(Paths.LogFolder);

                using (StreamWriter sw = new StreamWriter(logFilePath, true))
                {
                    sw.Write(errorInfo + "\n\n");
                }
            }
            catch (Exception ioEx)
            {
                ModConsole.Err("con.error.log_write_failed", ioEx.Message);
            }

            if (isCritical)
            {
                ModConsole.Err("con.error.occurred", logFilePath, message);
            }
            else
            {
                ModConsole.Warn("con.error.occurred_continue", logFilePath, message);
            }
        }

        public static void OpenCurrentSessionLogFolder()
        {
            if (Paths.LogDirectoryExists)
            {
                Process.Start(Paths.LogFolder);
            }
            else
            {
                ModUI.ShowMessage(Localization.Loc.T("msg.no_logs_folder"), Localization.Loc.T("msg.title"));
            }
        }

        public static void OpenOutputLog()
        {
            if (File.Exists(Paths.OutputLogPath))
            {
                Process.Start(Paths.OutputLogPath);
            }
            else
            {
                ModUI.ShowMessage(Localization.Loc.T("msg.no_output_log"), Localization.Loc.T("msg.title"));
            }
        }

        /// <summary>
        /// Dumps the info about the mod and lists all installed mods into MOP_REPORT.txt
        /// </summary>
        public static void GenerateReport()
        {
            string gameInfo = GetGameInfo();

            int reportsInFolder = 0;
            while (File.Exists($"{Paths.LogFolder}/{Paths.DefaultReportLogName}_{reportsInFolder}.txt"))
            {
                reportsInFolder++;
            }

            string path = $"{Paths.LogFolder}/{Paths.DefaultReportLogName}_{reportsInFolder}.txt";

            using (StreamWriter sw = new StreamWriter(path))
            {
                sw.Write(gameInfo);
            }

            ModConsole.Info("con.report_generated");
            Process.Start(path);
        }

        /// <summary>
        /// Generates the report about mod's settings and list of installed mods
        /// </summary>
        /// <returns></returns>
        internal static string GetGameInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Modern Optimization Plugin");
            sb.Append($"Version: ").AppendLine(MOP.ModVersion);
            sb.Append($"MSC Mod Loader Version: ").AppendLine(ModLoader.MSCLoader_Ver);
            sb.Append($"Date and Time: ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(GetSystemInfo());
            sb.Append($"Game restarts: ").Append(MopSettings.Restarts).AppendLine();
            sb.Append($"Game resolution: ").Append(Screen.width).Append("x").Append(Screen.height).AppendLine();
            sb.Append($"Screen resolution: ").Append(Screen.currentResolution.width).Append("x").Append(Screen.currentResolution.height).AppendLine();
            if (ModLoader.CurrentScene == CurrentScene.Game)
            {
                var elapsed = DateTime.Now.Subtract(SessionTimeStart);
                sb.Append("Session time: ").Append(elapsed.Hours).Append(" Hours ").Append(elapsed.Minutes).Append(" Minutes ").Append(elapsed.Seconds).AppendLine(" Seconds");
            }
            sb.Append("CPU: ").Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount).AppendLine(" cores)");
            sb.Append($"RAM: ").Append(SystemInfo.systemMemorySize).AppendLine(" MB");
            sb.Append($"GPU: ").Append(SystemInfo.graphicsDeviceName).Append(" (").Append(SystemInfo.graphicsMemorySize).AppendLine(" MB VRAM)");
            sb.AppendLine();
            sb.AppendLine("=== MOPR SETTINGS ===");
            sb.AppendLine(); 
            sb.Append("Language: ").Append(Localization.Loc.ToCode(Localization.Loc.Current)).AppendLine();
            sb.Append($"ActiveDistance: ").Append(MopMenu.ActiveDistance.GetValue()).AppendLine();
            sb.Append($"ActiveDistanceMultiplier: ").Append(MopSettings.ActiveDistanceMultiplicationValue).AppendLine();
            sb.Append($"PerformanceMode: ").Append(MopSettings.Mode).AppendLine();
            sb.Append($"LimitFramerate: ").Append(MopMenu.LimitFramerate.GetValue()).AppendLine();
            sb.Append($"FramerateLimiter: ").Append(MopMenu.FramerateLimiter.GetValue()).AppendLine();
            sb.Append($"ShadowDistance: ").Append(MopMenu.ShadowDistance.GetValue()).AppendLine();
            sb.Append($"RunInBackground: ").Append(MopMenu.KeepRunningInBackground.GetValue()).AppendLine();
            sb.Append($"DynamicDrawDistance: ").Append(MopMenu.DynamicDrawDistance.GetValue()).AppendLine();
            sb.Append($"RenderOptimizer: ").Append(MopMenu.RenderOptimizerEnabled.GetValue())
              .Append(" (culling: ").Append(MopMenu.RenderLayerCulling.GetValue())
              .Append(", quality: ").Append(MopMenu.RenderCullQuality.GetValue())
              .Append(", shadowLod: ").Append(MopMenu.RenderShadowLod.GetValue())
              .Append(", lampShadows: ").Append(MopMenu.RenderLightShadows.GetValue()).AppendLine(")");
            sb.Append($"BuiltWithStubs: ").Append(BuildInfo.BuiltWithStubs).AppendLine();
            sb.Append($"DestroyEmptyBottles: ").Append(MopMenu.DestroyEmptyBottles.GetValue()).AppendLine();
            sb.Append($"DisableEmptyItems: ").Append(MopMenu.DisableEmptyItems.GetValue()).AppendLine();
            sb.Append($"NoSkidmarks: ").Append(MopMenu.AlwaysDisableSkidmarks.GetValue()).AppendLine();
            sb.Append($"RuleFiles: ").Append(RulesManager.Book.Active.Count).Append(" active, ").Append(RulesManager.Book.RuleCount).AppendLine(" rules");
         
            // Game data
            if (ModLoader.CurrentScene == CurrentScene.Game)
            {
                sb.AppendLine();
                sb.AppendLine("=== GAME DATA ===");
                sb.AppendLine();
                sb.AppendLine(GetGameData("PlayerPosition"));
                sb.AppendLine(GetGameData("PlayerHasHayosikoKey"));
                sb.AppendLine(GetGameData("IsPlayerInCar"));
                sb.AppendLine(GetGameData("IsPlayerInSatsuma"));
                sb.AppendLine(GetGameData("DrawDistance"));
                sb.AppendLine(GetGameData("CanTriggerStatus"));
                sb.AppendLine(GetGameData("IsTrailerAttached"));
            }

            sb.AppendLine();
            sb.AppendLine("=== MESSAGES ===");
            sb.AppendLine();
            sb.AppendLine(string.Join("\n", ModConsole.GetMessages().ToArray()));

            // List installed mods.
            sb.AppendLine();
            sb.Append("=== MODS (").Append(ModLoader.LoadedMods.Count - 1).AppendLine(") ===");
            sb.AppendLine();
            foreach (var mod in ModLoader.LoadedMods)
            {
                // Ignore MSCLoader or MOP.
                if (mod.ID.Equals("MOP"))
                    continue;

                sb.AppendLine(mod.Name);
                sb.Append("  ID: ").AppendLine(mod.ID);
                sb.Append("  Version: ").AppendLine(mod.Version);
                sb.Append("  Author: ").AppendLine(mod.Author);
                sb.AppendLine();
            }

            // If only 3 mods have been found, that means the only mods active are MOP and two ModLoader modules.
            if (ModLoader.LoadedMods.Count <= 3)
            {
                sb.AppendLine("No other mods found");
            }

            // Rules: what is active, what is not and why, problems, and the user's own files.
            sb.AppendLine()
                .AppendLine("=== RULES ===")
                .AppendLine();
            try { sb.Append(RulesManager.ReportForBugReport()); }
            catch (Exception ex) { sb.AppendLine("Could not list the rules: " + ex.Message); }

            return sb.ToString();
        }

        static string GetGameData(string name)
        {
            string output = $"{name}: ";
            try
            {
                switch (name)
                {
                    default:
                        output += "null";
                        break;
                    case "PlayerPosition":
                        output += GameObject.Find("PLAYER").transform.position;
                        break;
                    case "PlayerHasHayosikoKey":
                        output += FSM.FsmManager.PlayerHasHayosikoKey();
                        break;
                    case "IsPlayerInCar":
                        output += FSM.FsmManager.IsPlayerInCar();
                        break;
                    case "IsPlayerInSatsuma":
                        output += FSM.FsmManager.IsPlayerInSatsuma();
                        break;
                    case "DrawDistance":
                        output += FSM.FsmManager.GetDrawDistance();
                        break;
                    case "CanTriggerStatus":
                        output += (Managers.ItemsManager.Instance == null 
                            ? "manager_null" 
                            : Managers.ItemsManager.Instance.GetCanTrigger() == null ? "null" 
                            : $"Found ({Managers.ItemsManager.Instance.GetCanTrigger().Path()})");
                        break;
                    case "IsTrailerAttached":
                        output += FSM.FsmManager.IsTrailerAttached();
                        break;
                }
            }
            catch
            {
                output += "error";
            }

            return output;
        }

        public static string GetSystemInfo()
        {
            string fullOS = SystemInfo.operatingSystem;
            string windowsVersion = GetWindowsName(fullOS);
            
            if (File.Exists("Z:/var/log/syslog"))
            {
                string name = GetLinuxName();
                return $"{name} [Wine, {windowsVersion}]";
            }
            else
            {
                return windowsVersion;
            }
        }

        private static string GetWindowsName(string fullOS)
        {
            int build = int.Parse(fullOS.Split('(')[1].Split(')')[0].Split('.')[2]);
            if (build > 9600)
            {
                string realOS = build >= 22000 ? $"Windows 11 (10.0.{build})" : $"Windows 10 (10.0.{build})";
                if (Directory.Exists("C:\\Program Files (Arm)"))
                {
                    realOS += " ARM";
                }
                else
                {
                    realOS += SystemInfo.operatingSystem.Contains("64bit") ? " 64bit" : " 32bit";
                }

                return realOS;
            }

            return fullOS;
        }

        private static string GetLinuxName()
        {
            string linuxInfoFile = "Z:/etc/os-release";
            string output = "Linux";
            if (File.Exists(linuxInfoFile))
            {
                StreamReader reader = new StreamReader(linuxInfoFile);
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (line.Contains("PRETTY_NAME"))
                    {
                        line = line.Replace("PRETTY_NAME=", "");
                        line = line.Replace("\"", "");
                        output = line;
                        break;
                    }
                }

                reader.Close();
            }
            else if (Directory.Exists("Z:/Applications"))
            {
                output = "macOS";
            }

            return output;
        }

        /// <summary>
        /// Deletes the entire logs folder.
        /// </summary>
        public static void DeleteAllLogs()
        {
            // MOP - REVIVAL: Directory.GetFiles threw when the logs folder was never created.
            string[] files = Paths.LogDirectoryExists ? Directory.GetFiles(Paths.LogFolder, "*.txt") : new string[0];

            if (files.Length == 0)
            {
                ModUI.ShowMessage(Localization.Loc.T("msg.no_logs"), Localization.Loc.T("msg.title"));
                return;
            }

            ModUI.ShowYesNoMessage(Localization.Loc.Plural("msg.delete_logs", files.Length), Localization.Loc.T("msg.title"), () => DeleteLogFiles(files));
        }

        private static void DeleteLogFiles(string[] files)
        {
            foreach (string file in files)
            {
                File.Delete(file);
            }
        }
    }
}
