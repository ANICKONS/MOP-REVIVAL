// MOP - REVIVAL
// Copyright(C) 2019-2023 Athlon (original Modern Optimization Plugin), 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
//
// Changes against the original MOP 3.10.4 entry point:
//   * ported to the MSCLoader 1.4 API (ModSetup + SetupFunction, new Settings API)
//   * Mod Loader Pro support removed (the loader is discontinued)
//   * built-in bug reporter replaced by MSCLoader's own bug report form
//   * rule files are bundled with the mod (no dead rule servers, no network access)
//   * new "Render optimizer" settings section
//   * 5.1: the settings page lives in MOP.cs; UI text is localized (MOP.Localization);
//     the loading screen no longer needs an asset bundle

using System;
using System.IO;
using System.Text;
using UnityEngine;
using MSCLoader;

using MOP.Common;
using MOP.Common.Enumerations;
using MOP.FSM;
using MOP.Helpers;
using MOP.Localization;
using MOP.Next;
using MOP.Rules;

namespace MOP
{
    public class MOP : Mod
    {
        public override string ID => "MOPR";
        public override string Name => "MOP - REVIVAL";
        public override string Author => "ANICKON";
        public override string Version => BuildInfo.Version;
        public const string SubVersion = ""; // BETA_x | RC_x
        public const string Edition = "MSCLoader";
        // MSCLoader reads it when it builds the mod list; ApplyLanguage also relabels a list already on screen.
        public override string Description => Loc.T("mod.description");
        public override byte[] Icon => Properties.Resources.icon;

        #region Settings & Configuration
        static string modConfigPath;
        public static string ModConfigPath => modConfigPath;

        static string modVersion = BuildInfo.Version;
        public static string ModVersion => modVersion + (SubVersion != "" ? "_" + SubVersion : "");
        public static string ModVersionShort => modVersion;

        private bool menuLoaded;
        #endregion

        public override void ModSetup()
        {
            SetupFunction(Setup.ModSettings, BuildSettings);
            SetupFunction(Setup.ModSettingsLoaded, OnSettingsLoaded);
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.PostLoad, OnPostLoad);
            SetupFunction(Setup.OnModEnabled, OnModEnabled);
        }

        void BuildSettings()
        {
            modVersion = Version;
            modConfigPath = ModLoader.GetModSettingsFolder(this);
            MopMenu.Build();
        }

        #region Lifecycle
        void OnMenuLoad()
        {
            menuLoaded = true;
            // MOP - REVIVAL 5.1 (fix): only the pre-save switched this off. Back in the menu without a save through
            // MOP (death, MOP's own scene restart, a scene change by another mod) it stayed on, and during the next
            // load Hypervisor.Update ran before MOP was set up: about 200 NullReferenceExceptions in the log.
            MopSettings.IsModActive = false;
            if (modConfigPath == null)
                modConfigPath = ModLoader.GetModSettingsFolder(this);

            RemoveUnusedFiles();

            if (!Version.Contains(MopSettings.Data.Version.ToString()))
            {
                MopSettings.Data.Version = Version;
                MopSettings.WriteData(MopSettings.Data);
                ModUI.ShowCustomMessage(Loc.F("msg.welcome", ModVersion), "MOP - REVIVAL",
                    new MsgBoxBtn[]
                    {
                        ModUI.CreateMessageBoxBtn(Loc.T("common.ok")),
                        ModUI.CreateMessageBoxBtn(Loc.T("msg.btn.changelog"), () =>
                        {
                            ModUI.ShowMessage(GetChangelog(true), Loc.F("msg.changelog_title", ModVersion.Replace("_", " ")));
                        }, true)
                    },
                    new MsgBoxBtn[] { });
            }

            FsmManager.ResetAll();
            Next.Engine.EngineLab.OnSceneChanged();
            Resources.UnloadUnusedAssets();
            GC.Collect();

            if (MenuHelper.Instance == null)
            {
                GameObject helper = new GameObject("MOP_MenuHelper");
                helper.AddComponent<MenuHelper>();
            }

            MopSettings.Restarts++;
            if (MopSettings.Restarts > MopSettings.MaxRestarts && !MopSettings.RestartWarningShown)
            {
                MopSettings.RestartWarningShown = true;
                ModUI.ShowMessage(Loc.F("msg.restarts", MopSettings.MaxRestarts), Loc.T("msg.title"));
            }

            if (MopSettings.GameFixStatus == GameFixStatus.DoFix)
            {
                MopSettings.GameFixStatus = GameFixStatus.Restarted;
                MenuHelper.Instance.RestartGame();
                return;
            }

            MopSettings.GameFixStatus = GameFixStatus.None;

            // Back in the main menu: a mode chosen in game applies now (Safe mode waits for this, as its message
            // says; before, it only applied after restarting the game).
            if (settingsLoaded) MopSettings.UpdatePerformanceMode();
            MopMenu.SetRestartNotice(false);

            if (!string.IsNullOrEmpty(SubVersion))
            {
                ModConsole.Log("<size=60><color=magenta>\n\nWARNING!\nYOU ARE USING A PRE-RELEASE VERSION OF MOP - REVIVAL!\n\n</color></size>");
            }

            if (BuildInfo.BuiltWithStubs)
            {
                ModConsole.LogWarning("[MOPR] This build was compiled against reference stubs instead of the real game assemblies. " +
                                      "It is meant for testing only.");
            }
        }

        void OnSettingsLoaded()
        {
            if (modConfigPath == null)
                modConfigPath = ModLoader.GetModSettingsFolder(this);

            // First: the language, so every message below is shown in it.
            MopMenu.OnSettingsLoaded();
            settingsLoaded = true;

            MopSettings.UpdateFramerateLimiter();
            MopSettings.UpdatePerformanceMode();
            MopSettings.UpdateShadows();
            MopSettings.UpdateMiscSettings();
            Next.Engine.EngineLab.ApplySettings();

            // In game (settings reset): read for the settings page only, the game keeps the rules it loaded with.
            RulesManager.Load(ModLoader.CurrentScene != CurrentScene.Game);
            if (!consoleCommandAdded)
            {
                // ModSettingsLoaded also runs after "Reset settings": register the command only once.
                consoleCommandAdded = true;
                ConsoleCommand.Add(new ConsoleCommands());
            }

            if (CompatibilityManager.IsConfilctingModPresent(out string modName))
            {
                ModUI.ShowMessage(Loc.F("msg.conflict", modName), Loc.T("msg.title"));
            }
            SaveManager.VerifySave();

            ModConsole.Log("[MOPR] MOP - REVIVAL " + MoprColors.Version("v" + ModVersion.Replace("_", " ")) + " " + MoprColors.Success("initialized!"));
        }

        static bool consoleCommandAdded;
        static bool settingsLoaded;

        /// <summary>
        /// Called once in MSCLoader's phase 3. The heavy initialization in Hypervisor waits until every
        /// remaining mod finished its phase 3 too (MOP 3.10 used the legacy SecondPassOnLoad for that).
        /// </summary>
        void OnPostLoad()
        {
            MopSettings.UpdateFramerateLimiter();
            MopSettings.UpdatePerformanceMode();
            MopSettings.UpdateShadows();
            MopSettings.UpdateMiscSettings();
            if (CompatibilityManager.IsConfilctingModPresent(out string modName))
            {
                ModConsole.LogError($"[MOPR] MOP - REVIVAL could not be loaded, because the following mod is present: <color=yellow>{modName}</color>");
                return;
            }

            Next.Engine.EngineLab.OnSceneChanged();

            GameObject mop = new GameObject("MOP");
            CompatibilityManager.Initialize();
            mop.AddComponent<Hypervisor>();
            mop.AddComponent<RenderOptimizer>();
            mop.AddComponent<VehicleMeshMerge>();

            // The mode the world is set up with (buildings, sectors, item LODs are built for it at load).
            MopSettings.LoadedMode = MopSettings.Mode;
            MopMenu.SetRestartNotice(true);
        }

        void OnModEnabled()
        {
            // The mod was enabled from the mod menu after the main menu was already loaded.
            if (!menuLoaded && ModLoader.CurrentScene == CurrentScene.MainMenu)
            {
                OnMenuLoad();
                OnSettingsLoaded();
            }
        }
        #endregion

        /// <summary>
        /// Gets changelog from changelog.txt and adds rich text elements.
        /// </summary>
        internal static string GetChangelog(bool useWelcomeScreenFormatting)
        {
            string raw = Properties.Resources.changelog;
            if (string.IsNullOrEmpty(raw))
                return "Dog ate my changelog :(";

            StringBuilder sb = new StringBuilder();
            string[] changelog = raw.Replace("\r", "").Split('\n');

            bool skipNext = false;
            for (int i = 0; i < changelog.Length; i++)
            {
                if (skipNext)
                {
                    skipNext = false;
                    continue;
                }
                string line = changelog[i];

                if (line.StartsWith("###"))
                {
                    line = line.Replace("###", "");
                    if (useWelcomeScreenFormatting)
                    {
                        line = $"<color=yellow>{line}</color>";
                        skipNext = true;
                    }
                    else
                    {
                        line = $"<color=yellow><size=24>{line}</size></color>";
                    }
                }

                if (line.StartsWith("  -"))
                    line = "    • " + line.Substring(3);
                else if (line.StartsWith("-"))
                    line = "• " + line.Substring(1);

                line = line.Replace("(My Summer Car Bug)", "<color=green>My Summer Car Bug: </color>")
                           .Replace("(My Summer Car)", "<color=green>My Summer Car: </color>")
                           .Replace("Rule Files API:", "<color=cyan>Rule Files API:</color>")
                           .Replace("(MSCLoader)", "<color=yellow>MSCLoader:</color>");

                sb.AppendLine(line);

                if (i >= 20 && useWelcomeScreenFormatting)
                {
                    sb.Append("..and more.");
                    break;
                }
            }

            return sb.ToString();
        }

        void RemoveUnusedFiles()
        {
            RemoveIfExists(Path.Combine(ModConfigPath, "LastModList.mop"));
            RemoveIfExists(Path.Combine(ModConfigPath, "LastUpdate.mop"));
            RemoveIfExists(Path.Combine(ModConfigPath, "RulesInfo.json"));
        }

        void RemoveIfExists(string filename)
        {
            if (File.Exists(filename))
                File.Delete(filename);
        }
    }
}
