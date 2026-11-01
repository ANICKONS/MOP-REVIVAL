// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Restart guard — a generalisation of MOP's dozens of hand-written "RestartOnEnable = false" fixes.
//
// Verified against the PlayMaker 1.6 assembly shipped with My Summer Car:
//   Fsm.OnDisable -> Stop(): if (RestartOnEnable) StopAndReset();   // exits the state, ActiveState = null
//   Fsm.OnEnable():          if (ActiveState == null || RestartOnEnable) { ActiveState = start; Start(); }
// So when MOP disables an object and enables it again, every FSM inside it with RestartOnEnable = true
// starts over from its start state. If that start state (or the states right after it) contains
// Easy Save 2 "Load*" actions, the FSM re-reads the LAST SAVE and silently reverts the player's
// progress: paint colours, fluid levels, door states, bolts, kilju... MOP 3.10 found these one by one
// from bug reports. With RestartOnEnable = false PlayMaker keeps the FSM paused in its current state,
// which is exactly what vanilla does: in vanilla those objects are never disabled at all.
//
// Scope (strict, to stay vanilla for everything that is not MOP's own toggle):
//   * Only objects MOP really deactivates: called right before MOP's own SetActive(false) /
//     PlayMakerFSM.enabled = false, never from a "would disable" decision that may still bail out.
//   * Only FSMs that are running at that moment (inactive children are controlled by the game).
//   * Only FSMs whose restart path reads the save file.
//   * Only for MOP's disable/enable window: right after MOP re-enables the object, the original
//     RestartOnEnable = true is restored, so disable/enable cycles done by the game itself keep the
//     vanilla restart behaviour.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using HutongGames.PlayMaker;

using MOP.Common;

namespace MOP.Next
{
    static class RestartGuard
    {
        // Easy Save 2 PlayMaker actions that read the save file (Assembly-CSharp, HutongGames.PlayMaker.Actions).
        static readonly HashSet<string> SaveLoadActions = new HashSet<string>
        {
            "LoadInt", "LoadFloat", "LoadBool", "LoadString", "LoadVector2", "LoadVector3", "LoadColor",
            "LoadTexture", "LoadQuaternion", "LoadTransform", "LoadMeshCollider", "LoadBoxCollider",
            "LoadSphereCollider", "LoadCapsuleCollider", "LoadAudioClip", "LoadMaterial",
            "ArrayListEasyLoad", "ArrayListEasyLoadEncrypted", "HashTableEasyLoad", "HashTableEasyLoadEncrypted"
        };

        // Restart path = start state plus the states reachable from it within this many transitions
        // (any event: FINISHED and the true/false events of the comparison actions that usually precede a load).
        const int MaxRestartDepth = 3;
        const int MaxRestartPathStates = 12;

        // FSMs currently switched to pause semantics, per MOP-disabled root (GameObject or FSM component).
        static readonly Dictionary<int, List<Fsm>> suspended = new Dictionary<int, List<Fsm>>();
        // Cached analysis per FSM component instance: true = restart reads the save.
        static readonly Dictionary<int, bool> analysis = new Dictionary<int, bool>();
        static readonly Dictionary<Type, bool> actionTypeCache = new Dictionary<Type, bool>();
        static readonly Dictionary<int, string> guardedLog = new Dictionary<int, string>();
        static readonly List<PlayMakerFSM> fsmBuffer = new List<PlayMakerFSM>();

        public static int GuardedCount => guardedLog.Count;
        public static int SuspendedRoots => suspended.Count;

        public static bool Enabled => MopMenu.RestartGuardEnabled == null || MopMenu.RestartGuardEnabled.GetValue();

        /// <summary>Clears per-scene state. Called when MOP initializes in a new game scene.</summary>
        public static void Reset()
        {
            suspended.Clear();
            analysis.Clear();
            guardedLog.Clear();
        }

        // ------------------------------------------------------------------------------------------------
        // Window API
        // ------------------------------------------------------------------------------------------------

        /// <summary>Call right before MOP deactivates <paramref name="root"/> with SetActive(false).</summary>
        public static void BeforeDisable(GameObject root, string owner)
        {
            if (!Enabled || root == null || !root.activeInHierarchy) return;

            try
            {
                int id = root.GetInstanceID();
                if (suspended.ContainsKey(id)) return; // already inside a MOP disable window

                PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(false); // running objects only
                List<Fsm> list = null;
                for (int i = 0; i < fsms.Length; i++)
                {
                    Fsm f = Suspend(fsms[i], owner);
                    if (f == null) continue;
                    if (list == null) list = new List<Fsm>();
                    list.Add(f);
                }

                if (list != null) suspended[id] = list;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RESTART_GUARD_BEFORE_DISABLE");
            }
        }

        /// <summary>Call right after MOP re-activated <paramref name="root"/> with SetActive(true).</summary>
        public static void AfterEnable(GameObject root)
        {
            if (root == null) return;
            Release(root.GetInstanceID());
        }

        /// <summary>Call right before MOP sets <c>fsm.enabled = false</c>.</summary>
        public static void BeforeDisableComponent(PlayMakerFSM fsm, string owner)
        {
            if (!Enabled || fsm == null || !fsm.enabled || !fsm.gameObject.activeInHierarchy) return;

            try
            {
                int id = fsm.GetInstanceID();
                if (suspended.ContainsKey(id)) return;
                Fsm f = Suspend(fsm, owner);
                if (f != null) suspended[id] = new List<Fsm> { f };
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RESTART_GUARD_BEFORE_DISABLE_FSM");
            }
        }

        /// <summary>Call right after MOP sets <c>fsm.enabled = true</c>.</summary>
        public static void AfterEnableComponent(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            Release(fsm.GetInstanceID());
        }

        /// <summary>Restores every suspended FSM (pre-save safety net and mod shutdown).</summary>
        public static void ReleaseAll()
        {
            List<int> keys = new List<int>(suspended.Keys);
            for (int i = 0; i < keys.Count; i++)
                Release(keys[i]);
        }

        static Fsm Suspend(PlayMakerFSM fsm, string owner)
        {
            if (fsm == null || !fsm.enabled) return null;
            Fsm f = fsm.Fsm;
            if (f == null || !f.RestartOnEnable) return null; // MOP's own fixes or the game already pause it

            int id = fsm.GetInstanceID();
            if (!analysis.TryGetValue(id, out bool hazard))
            {
                string reason = FindSaveLoadInRestartPath(fsm, f, out bool conclusive);
                hazard = reason != null;
                if (conclusive || hazard) analysis[id] = hazard;
                if (hazard && !guardedLog.ContainsKey(id))
                    guardedLog[id] = $"{fsm.transform.Path()} | FSM \"{fsm.FsmName}\" | {reason} | {owner}";
            }

            if (!hazard) return null;
            f.RestartOnEnable = false;
            return f;
        }

        static void Release(int id)
        {
            if (!suspended.TryGetValue(id, out List<Fsm> list)) return;
            suspended.Remove(id);

            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    if (list[i] != null && !list[i].RestartOnEnable)
                        list[i].RestartOnEnable = true;
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Analysis
        // ------------------------------------------------------------------------------------------------

        public static string FindSaveLoadInRestartPath(PlayMakerFSM fsm, Fsm f) => FindSaveLoadInRestartPath(fsm, f, out _);

        /// <summary>
        /// Returns "state/action" of the first save-load action PlayMaker may run when restarting this FSM,
        /// or null. <paramref name="conclusive"/> is false when some state could not be inspected yet
        /// (FSM not initialised), so the result should not be cached.
        /// </summary>
        public static string FindSaveLoadInRestartPath(PlayMakerFSM fsm, Fsm f, out bool conclusive)
        {
            conclusive = true;
            FsmState[] states = fsm.FsmStates;
            if (states == null || states.Length == 0) return null;

            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            HashSet<string> visited = new HashSet<string>();
            queue.Enqueue(new KeyValuePair<string, int>(f.StartState, 0));

            while (queue.Count > 0 && visited.Count < MaxRestartPathStates)
            {
                KeyValuePair<string, int> next = queue.Dequeue();
                if (string.IsNullOrEmpty(next.Key) || !visited.Add(next.Key)) continue;

                FsmState state = FindState(states, next.Key);
                if (state == null) continue;
                if (state.Fsm == null)
                {
                    // Not initialised: reading Actions would make PlayMaker log errors. Try again later.
                    conclusive = false;
                    continue;
                }

                FsmStateAction[] actions = state.Actions;
                if (actions != null)
                {
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] != null && IsSaveLoadAction(actions[a].GetType()))
                            return state.Name + "/" + actions[a].GetType().Name;
                    }
                }

                if (next.Value >= MaxRestartDepth) continue;
                FsmTransition[] transitions = state.Transitions;
                if (transitions == null) continue;
                for (int t = 0; t < transitions.Length; t++)
                {
                    if (transitions[t] != null && !string.IsNullOrEmpty(transitions[t].ToState))
                        queue.Enqueue(new KeyValuePair<string, int>(transitions[t].ToState, next.Value + 1));
                }
            }

            return null;
        }

        static FsmState FindState(FsmState[] states, string name)
        {
            for (int i = 0; i < states.Length; i++)
                if (states[i] != null && states[i].Name == name)
                    return states[i];
            return null;
        }

        static bool IsSaveLoadAction(Type type)
        {
            if (actionTypeCache.TryGetValue(type, out bool cached)) return cached;

            bool result = type.Namespace == "HutongGames.PlayMaker.Actions" && SaveLoadActions.Contains(type.Name);
            if (!result && type.Name.IndexOf("Load", StringComparison.Ordinal) >= 0)
            {
                // Unknown loader (e.g. added by a mod): accept it if it is in the "Easy Save 2" action category.
                try
                {
                    foreach (object attr in type.GetCustomAttributes(true))
                    {
                        if (attr.GetType().Name != "ActionCategoryAttribute") continue;
                        object category = attr.GetType().GetProperty("Category")?.GetValue(attr, null);
                        if (category is string s && s == "Easy Save 2") { result = true; break; }
                    }
                }
                catch { }
            }

            actionTypeCache[type] = result;
            return result;
        }

        /// <summary>Writes the list of FSMs the guard has protected so far and returns the path.</summary>
        public static string WriteReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"MOP - REVIVAL {MOP.ModVersion} - restart guard ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"Enabled: {Enabled}. {GuardedCount} FSMs are paused (not reset) while MOPR has their object disabled. " +
                          $"Objects disabled right now: {SuspendedRoots}.");
            sb.AppendLine();
            foreach (string line in guardedLog.Values)
                sb.AppendLine(line);

            string path = Path.Combine(Paths.LogFolder, $"MOP_RestartGuard_{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
