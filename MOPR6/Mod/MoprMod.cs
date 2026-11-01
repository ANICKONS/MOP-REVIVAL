// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Entry point (design §1). Refuses to run next to MOPR 5.x (requirement 1.2), reports other mods once (2.3), builds
// the settings page (13), sets up the log, the ledger, the save barrier and the system host in the game scene, and
// installs the systems as the settings page says. The test probe is registered but only installed on request.
//
// MSCLoader 1.4 order: ModSetup -> ModSettings (the page) -> OnMenuLoad -> the saved values -> ModSettingsLoaded;
// PostLoad in every game scene.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using MOPR6.Core;
using MOPR6.Diagnostics;
using MOPR6.Host;
using MOPR6.Ledger;
using MOPR6.Localization;
using MOPR6.Systems;
using MOPR6.UI;
using MSCLoader;
using UnityEngine;

namespace MOPR6
{
    public class MoprMod : Mod
    {
        public const string ModId = "MOPR6";
        /// <summary>MOPR 5.x (and MOP - REVIVAL 4.x/5.x): never together with MOPR6.</summary>
        public const string LegacyModId = "MOPR";

        public override string ID => ModId;
        public override string Name => "MOPR6";
        public override string Author => "ANICKON";
        public override string Version => BuildInfo.Version;
        public override string Description => Loc.T("mod.description");

        internal static MoprMod Instance { get; private set; }

        /// <summary>MOPR 5.x is loaded too: MOPR6 does nothing in this session.</summary>
        public static bool Conflict5x { get; private set; }

        /// <summary>Lives across scenes (DontDestroyOnLoad): runs the update check's coroutine.</summary>
        internal static MonoBehaviour MenuHost { get; private set; }

#if DEBUG
        const bool DebugBuild = true;
#else
        const bool DebugBuild = false;
#endif

        static readonly ModVersion version = ModVersion.Parse(BuildInfo.Version);
        static bool otherModsReported, commandAdded, systemsRegistered;

        public override void ModSetup()
        {
            // MSCLoader sets the mods up again when the game goes back to the main menu (a new MoprMod instance, the
            // same static state): the log, the systems and the console command stay as they are.
            Instance = this;
            ModLog.Open(ModLoader.GetModSettingsFolder(this));
            if (!systemsRegistered)
            {
                systemsRegistered = true;
                // Before the settings page: it lists the systems.
                SystemHost.Register(Engine.FsmFastPath.Instance);
                SystemHost.Register(Engine.FsmPropertyFastPath.Instance);
                SystemHost.Register(Engine.RedundantWrites.Instance);
                SystemHost.Register(Engine.IkFastPath.Instance);
                SystemHost.Register(Engine.SharedMouseRay.Instance);
                SystemHost.Register(Render.RenderLayers.Instance);
                SystemHost.Register(Render.RenderShadows.Instance);
                SystemHost.Register(Render.RenderLamps.Instance);
                SystemHost.Register(Render.SatsumaMerge.Instance);
                SystemHost.Register(Sleep.FsmSleep.Instance);
                SystemHost.Register(World.ParkedVehicles.Instance);
                // After the parked vehicles (the save barrier suspends in reverse order: parts on before the vehicles thaw).
                SystemHost.Register(World.Dormancy.Instance);
                SystemHost.Register(Experimental.MirrorRate.Instance);
                SystemHost.Register(Experimental.EngineBayCulling.Instance);
                SystemHost.Register(ProbeSystem.Instance);
            }
            else ModLog.Line("mods set up again (back in the main menu)");
            SetupFunction(Setup.ModSettings, OnModSettings);
            SetupFunction(Setup.ModSettingsLoaded, OnSettingsLoaded);
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.PostLoad, OnPostLoad);
        }

        void OnModSettings()
        {
            // Every mod is loaded by now: the page can say whether 5.x is there.
            Conflict5x = LegacyLoaded();
            try { SettingsPage.Build(); }
            catch (Exception ex)
            {
                // The page stays incomplete; the systems keep their defaults and the console still works.
                ModLog.Report("settings/Build", ex);
            }
        }

        void OnSettingsLoaded()
        {
            try { SettingsPage.OnLoaded(); }
            catch (Exception ex) { ModLog.Report("settings/Loaded", ex); }
        }

        void OnMenuLoad()
        {
            Conflict5x = LegacyLoaded();
            ModConsole.Print("<color=orange>" + Loc.F("con.loaded", version, version.IsPreview ? Loc.T("con.preview_mark") : "") + "</color>");
            if (Conflict5x) WarnConflict();
            // "mopr" is the console command of 5.x too: registered only when 5.x is not loaded.
            if (!commandAdded && !Conflict5x)
            {
                commandAdded = true;
                ConsoleCommand.Add(new MoprCommand());
            }
            if (MenuHost == null)
            {
                GameObject go = new GameObject("MOPR_Menu");
                UnityEngine.Object.DontDestroyOnLoad(go);
                MenuHost = go.AddComponent<MenuBehaviour>();
            }
            if (!Conflict5x) UpdateChecker.OnMenuLoad(MenuHost);
        }

        void OnPostLoad()
        {
            // Settings or the mod list may have changed in the menu.
            Conflict5x = LegacyLoaded();
            if (Conflict5x)
            {
                WarnConflict();
                return;
            }
            ReportOtherMods();
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Arbiters.StartScene(DebugBuild);
                SaveHooks.StartScene();
                Engine.FsmIdle.Clear();
                Render.RenderScan.StartScene();
                Catalog.Vanilla.StartScene();
                string inv = Catalog.Vanilla.Load();
                if (inv != null) ModLog.Warn("inventory not loaded: " + inv + " - systems that need it stay off");
                string err = Engine.FsmHooks.Install();
                if (err != null) ModLog.Warn("FSM hook not installed: " + err + " - MOPR6 stays off (the save barrier needs it)");
                else
                {
                    WriterHooks.Install();
                    SystemHost.StartScene();
                    SystemHost.MarkReady();
                    SettingsPage.ApplyAtLoad();
                }
                ModLog.Line("game scene set up (ledger, save barrier, host) in " + watch.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                ModLog.Report("setup/PostLoad", ex);
            }
        }

        static bool LegacyLoaded()
        {
            foreach (Mod m in ModLoader.LoadedMods)
                if (m != null && !m.isDisabled && string.Equals(m.ID, LegacyModId, StringComparison.Ordinal)) return true;
            return false;
        }

        static void WarnConflict()
        {
            ModConsole.Warning("MOPR6: " + Loc.T("con.conflict"));
        }

        void ReportOtherMods()
        {
            if (otherModsReported) return;
            otherModsReported = true;
            List<string> others = new List<string>();
            foreach (Mod m in ModLoader.LoadedMods)
            {
                if (m == null || m.isDisabled) continue;
                string id = m.ID ?? "";
                if (id == ModId || id.StartsWith("MSCLoader_", StringComparison.Ordinal) || id == "MOPR_TestBot") continue;
                others.Add(id);
            }
            if (others.Count > 0)
                ModConsole.Print("MOPR6: " + Loc.F("con.other_mods", others.Count, string.Join(", ", others.ToArray())));
        }

        sealed class MenuBehaviour : MonoBehaviour { }
    }
}
