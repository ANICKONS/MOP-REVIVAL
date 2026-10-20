// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Read-only diagnostics. Nothing in this file changes game state.
//
//  * FsmAudit: the #1 bug class of distance-based optimizers is "a PlayMaker FSM restarts from its start
//    state when its GameObject is re-enabled", which re-runs "Load" states and resets progress (paint,
//    fluids, bolts, kilju...). MOP 3.10 fixed every known case by hand (RestartOnEnable = false).
//    The audit scans every object MOP toggles and lists the FSMs that would still restart and contain
//    load-like states, so new cases (game updates, mods) can be found in minutes instead of bug reports.
//
//  * SaveBarrier: after MOP re-enables everything before the game saves, verifies that nothing managed
//    by MOP is still disabled and writes the result to the console/log.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using HutongGames.PlayMaker;

using MOP.Common;
using MOP.Items;
using MOP.Managers;
using MOP.Places;
using MOP.Vehicles;
using MOP.WorldObjects;

namespace MOP.Next
{
    static class FsmAudit
    {
        static readonly string[] LoadLikeStateNames = { "Load", "State 1", "Restore", "Init" };

        /// <summary>Runs the audit and writes the report to the MOP_Logs folder. Returns the report path.</summary>
        public static string Run()
        {
            HashSet<GameObject> roots = CollectManagedRoots(out Dictionary<GameObject, string> owners);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"MOP - REVIVAL {MOP.ModVersion} - FSM restart audit ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine("FSMs below live inside objects that MOPR enables/disables and still have RestartOnEnable = true.");
            sb.AppendLine("Those with load-like states may reset their progress whenever MOPR re-enables the object.");
            sb.AppendLine();

            int scanned = 0, restarting = 0, hazards = 0;
            HashSet<PlayMakerFSM> seen = new HashSet<PlayMakerFSM>();

            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                PlayMakerFSM[] fsms;
                try { fsms = root.GetComponentsInChildren<PlayMakerFSM>(true); }
                catch { continue; }

                foreach (PlayMakerFSM fsm in fsms)
                {
                    if (fsm == null || !seen.Add(fsm)) continue;
                    scanned++;

                    bool restarts;
                    try { restarts = fsm.Fsm.RestartOnEnable; }
                    catch { continue; }
                    if (!restarts) continue;
                    restarting++;

                    // Exact check: does PlayMaker's restart path (start state + FINISHED fall-through) read the save?
                    string loadAction = null;
                    try { loadAction = RestartGuard.FindSaveLoadInRestartPath(fsm, fsm.Fsm); }
                    catch { }

                    string owner = owners.TryGetValue(root, out string o) ? o : "?";
                    if (loadAction != null)
                    {
                        hazards++;
                        sb.Append("[HAZARD] ").Append(fsm.transform.Path()).Append(" | FSM \"").Append(fsm.FsmName).Append("\"")
                          .Append(" | restart reads the save: ").Append(loadAction)
                          .Append(" | managed by: ").Append(owner).AppendLine();
                        continue;
                    }

                    // Weaker signal: load-like state names (may be reached later through events).
                    string loadStates = FindLoadLikeStates(fsm);
                    if (loadStates.Length == 0) continue;
                    sb.Append("[suspect] ").Append(fsm.transform.Path()).Append(" | FSM \"").Append(fsm.FsmName).Append("\"")
                      .Append(" | states: ").Append(loadStates)
                      .Append(" | managed by: ").Append(owner).AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine($"Scanned {scanned} FSMs in {roots.Count} managed objects: {restarting} restart on enable, {hazards} potential hazards.");

            string path = Path.Combine(Paths.LogFolder, $"MOP_FsmAudit_{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt");
            File.WriteAllText(path, sb.ToString());
            ModConsole.Info("con.audit", scanned, hazards, path);
            return path;
        }

        static string FindLoadLikeStates(PlayMakerFSM fsm)
        {
            List<string> found = new List<string>();
            try
            {
                FsmState[] states = fsm.FsmStates;
                if (states == null) return "";
                foreach (FsmState state in states)
                {
                    if (state == null) continue;
                    string name = state.Name ?? "";
                    foreach (string candidate in LoadLikeStateNames)
                    {
                        if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found.Add(name);
                            break;
                        }
                    }
                }
            }
            catch { }
            return string.Join(", ", found.ToArray());
        }

        static HashSet<GameObject> CollectManagedRoots(out Dictionary<GameObject, string> owners)
        {
            HashSet<GameObject> roots = new HashSet<GameObject>();
            owners = new Dictionary<GameObject, string>();

            Dictionary<GameObject, string> ownerMap = owners;
            void Add(GameObject g, string owner)
            {
                if (g == null) return;
                if (roots.Add(g)) ownerMap[g] = owner;
            }

            try
            {
                foreach (ItemBehaviour item in ItemsManager.Instance.GetAll)
                    if (item != null) Add(item.gameObject, "Items");
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "FSM_AUDIT_ITEMS"); }

            try
            {
                foreach (Vehicle v in VehicleManager.Instance.GetAll)
                    if (v != null && v.gameObject != null) Add(v.gameObject, "Vehicles");
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "FSM_AUDIT_VEHICLES"); }

            try
            {
                foreach (Place p in PlaceManager.Instance.GetAll)
                {
                    if (p == null || p.DisableableChilds == null) continue;
                    foreach (Transform t in p.DisableableChilds)
                        if (t != null) Add(t.gameObject, "Place " + p.GetName());
                }
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "FSM_AUDIT_PLACES"); }

            try
            {
                foreach (GenericObject w in WorldObjectManager.Instance.GetAll)
                    if (w != null) Add(w.GameObject, "World objects");
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "FSM_AUDIT_WORLD"); }

            return roots;
        }
    }

    static class SaveBarrier
    {
        /// <summary>
        /// Verifies that everything MOP manages is enabled again, right after the pre-save ToggleAll.
        /// Only logs; never changes state.
        /// </summary>
        public static void Verify()
        {
            int inactiveItems = 0, inactivePlaces = 0, inactiveVehicles = 0;
            StringBuilder details = new StringBuilder();

            try
            {
                foreach (ItemBehaviour item in ItemsManager.Instance.GetAll)
                {
                    if (item == null || item.gameObject == null) continue;
                    if (!item.gameObject.activeSelf)
                    {
                        inactiveItems++;
                        if (inactiveItems <= 10) details.Append("  item: ").AppendLine(item.transform.Path());
                    }
                }

                foreach (Place place in PlaceManager.Instance.GetAll)
                {
                    if (place?.DisableableChilds == null) continue;
                    foreach (Transform t in place.DisableableChilds)
                    {
                        if (t != null && !t.gameObject.activeSelf)
                        {
                            inactivePlaces++;
                            if (inactivePlaces <= 10) details.Append("  place child: ").AppendLine(t.Path());
                        }
                    }
                }

                foreach (Vehicle v in VehicleManager.Instance.GetAll)
                {
                    if (v == null || v.gameObject == null || v.IsPassive) continue;
                    if (!v.gameObject.activeSelf)
                    {
                        inactiveVehicles++;
                        details.Append("  vehicle: ").AppendLine(v.gameObject.name);
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SAVE_BARRIER_VERIFY");
                return;
            }

            if (inactiveItems + inactivePlaces + inactiveVehicles == 0)
            {
                ModConsole.Debug("Save barrier: all managed objects are enabled.");
                return;
            }

            // Some objects are legitimately inactive (e.g. the game itself hid an item, "empty" items).
            ModConsole.Warn("con.save.barrier", inactiveItems, inactivePlaces, inactiveVehicles, details);
        }
    }
}
