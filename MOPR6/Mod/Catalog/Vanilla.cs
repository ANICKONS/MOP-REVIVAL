// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// "Is this FSM vanilla?" (requirements 2.1, 2.2; design §5): MOPR6's optimizations only touch FSMs whose structure
// is in the inventory of the vanilla game (Resources/catalog/inventory.json, made by "EquivCompare catalog build"
// from the TestBot dump). The structure is hashed here exactly as the dump does it (TestBot\Inventory.cs):
// action types from the loaded actions, or from the serialized ActionData when the state has not loaded them;
// a disabled action with a leading '-'; transitions "EVENT>State"; global transitions. A mod that inserted an action
// (MSCLoader FsmHook replaces state.Actions), added a state or changed a transition changes the hash.
// The exact optimizations are exact for any FSM; the inventory is the policy of 2.1 (only what we know). The answer
// is cached per FSM and the cache is checked again in the background (Sweep), so a mod that changes an FSM later is
// seen within one sweep.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using MOPR6.Core.Catalog;
using MOPR6.Diagnostics;

namespace MOPR6.Catalog
{
    public static class Vanilla
    {
        /// <summary>The distinct vanilla FSM structures (EquivCompare catalog build -> Resources/catalog/fsmkeys.txt).</summary>
        public const string ResourceName = "MOPR6.catalog.fsmkeys.txt";

        static HashSet<string> fsmKeys;
        /// <summary>Hash of the game's Assembly-CSharp.dll the inventory was made from.</summary>
        public static string InventoryGame { get; private set; }
        public static string Error { get; private set; } = "not loaded";
        public static bool Ready => fsmKeys != null;
        public static int Checked, Accepted, Rejected;

        sealed class RefComparer : IEqualityComparer<PlayMakerFSM>
        {
            public bool Equals(PlayMakerFSM a, PlayMakerFSM b) => ReferenceEquals(a, b);
            public int GetHashCode(PlayMakerFSM o) => RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>The cached answer for one FSM; the sweep updates it in place, so holders see a change.</summary>
        public sealed class Verdict
        {
            public bool Value;
        }

        static readonly Dictionary<PlayMakerFSM, Verdict> cache = new Dictionary<PlayMakerFSM, Verdict>(8192, new RefComparer());
        static readonly List<PlayMakerFSM> sweepList = new List<PlayMakerFSM>();
        static int sweepIndex;

        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo actionNamesField = typeof(ActionData).GetField("actionNames", I);
        static readonly FieldInfo actionEnabledField = typeof(ActionData).GetField("actionEnabled", I);

        /// <summary>Loads the embedded inventory once. Null on success, else the reason (Error).</summary>
        public static string Load()
        {
            if (fsmKeys != null) return null;
            try
            {
                Stream s = typeof(Vanilla).Assembly.GetManifestResourceStream(ResourceName);
                if (s == null) return Error = "no inventory in this build";
                HashSet<string> keys;
                string game;
                using (StreamReader r = new StreamReader(s)) keys = InventoryIO.ReadFsmKeys(r, out game);
                InventoryGame = game;
                fsmKeys = keys;
                Error = null;
                string live = GameAssemblyHash();
                ModLog.Line("inventory: " + keys.Count + " vanilla FSM structures, game " + (game ?? "?") +
                            (live != null && game != null && !string.Equals(live, game, StringComparison.OrdinalIgnoreCase) ? " - THIS GAME IS " + live : ""));
                if (live != null && game != null && !string.Equals(live, game, StringComparison.OrdinalIgnoreCase))
                    ModLog.Warn("the game was updated since this MOPR6 build: FSMs that changed are left alone");
                return null;
            }
            catch (Exception ex)
            {
                ModLog.Report("catalog/Load", ex);
                return Error = ex.GetType().Name + ": " + ex.Message;
            }
        }

        static string Key(string fsmName, string hash) => InventoryIO.FsmKey(fsmName, hash);

        static string liveGameHash;

        /// <summary>MD5 of the game's Assembly-CSharp.dll (as the TestBot dump), or null.</summary>
        static string GameAssemblyHash()
        {
            if (liveGameHash != null) return liveGameHash;
            liveGameHash = ComputeGameHash();
            return liveGameHash;
        }

        static string ComputeGameHash()
        {
            try
            {
                string path = typeof(PlayMakerFSM).Assembly.Location;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Assembly-CSharp") { path = a.Location; break; }
                if (string.IsNullOrEmpty(path) || !path.EndsWith("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase)) return null;
                using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                    return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
            }
            catch (Exception ex)
            {
                ModLog.Report("catalog/GameHash", ex);
                return null;
            }
        }

        /// <summary>A new scene: FSMs of the old one are gone.</summary>
        public static void StartScene()
        {
            cache.Clear();
            sweepList.Clear();
            sweepIndex = 0;
        }

        /// <summary>True if the FSM's structure is in the vanilla inventory (cached per FSM).</summary>
        public static bool IsVanilla(PlayMakerFSM pm) => For(pm).Value;

        /// <summary>The cached verdict of an FSM (computed on first use).</summary>
        public static Verdict For(PlayMakerFSM pm)
        {
            Verdict v;
            if (cache.TryGetValue(pm, out v)) return v;
            v = new Verdict { Value = Compute(pm) };
            if (cache.Count > 30000) { cache.Clear(); sweepList.Clear(); sweepIndex = 0; }
            cache[pm] = v;
            sweepList.Add(pm);
            return v;
        }

        static bool Compute(PlayMakerFSM pm)
        {
            Checked++;
            bool v = fsmKeys != null && pm != null && pm.Fsm != null && fsmKeys.Contains(Key(pm.FsmName, Fingerprint.Of(Shape(pm))));
            if (v) Accepted++;
            else Rejected++;
            return v;
        }

        /// <summary>
        /// Background check of the cache, <paramref name="budget"/> FSMs per call: a structure that changed since it
        /// was cached (a mod injected an action) is cached again with the new answer. Returns how many changed.
        /// </summary>
        public static int Sweep(int budget)
        {
            int changed = 0;
            for (int n = 0; n < budget && sweepList.Count > 0; n++)
            {
                if (sweepIndex >= sweepList.Count) sweepIndex = 0;
                PlayMakerFSM pm = sweepList[sweepIndex];
                if (pm == null)
                {
                    // Destroyed: drop it (swap with the last one).
                    cache.Remove(pm);
                    sweepList[sweepIndex] = sweepList[sweepList.Count - 1];
                    sweepList.RemoveAt(sweepList.Count - 1);
                    continue;
                }
                bool now = Compute(pm);
                Verdict v;
                if (!cache.TryGetValue(pm, out v)) cache[pm] = v = new Verdict { Value = now };
                else if (v.Value != now)
                {
                    changed++;
                    if (!now) ModLog.Line("not vanilla any more (changed by a mod?): " + Path(pm));
                    v.Value = now;
                }
                sweepIndex++;
            }
            return changed;
        }

        /// <summary>The structure of a live FSM, built like the TestBot inventory dump.</summary>
        public static FsmShape Shape(PlayMakerFSM pm)
        {
            Fsm fsm = pm.Fsm;
            FsmShape shape = new FsmShape { Name = pm.FsmName, StartState = fsm.StartState };
            FsmState[] states = fsm.States ?? new FsmState[0];
            foreach (FsmState s in states)
            {
                StateShape st = new StateShape { Name = s.Name };
                if (s.ActionsLoaded)
                {
                    foreach (FsmStateAction a in s.Actions) st.Actions.Add(a == null ? "null" : (a.Enabled ? "" : "-") + a.GetType().ToString());
                }
                else if (s.ActionData != null)
                {
                    List<string> names = actionNamesField != null ? actionNamesField.GetValue(s.ActionData) as List<string> : null;
                    List<bool> enabled = actionEnabledField != null ? actionEnabledField.GetValue(s.ActionData) as List<bool> : null;
                    if (names != null)
                        for (int i = 0; i < names.Count; i++)
                            st.Actions.Add((enabled == null || i >= enabled.Count || enabled[i] ? "" : "-") + names[i]);
                }
                foreach (FsmTransition t in s.Transitions ?? new FsmTransition[0]) st.Transitions.Add(t.EventName + ">" + t.ToState);
                shape.States.Add(st);
            }
            foreach (FsmTransition t in fsm.GlobalTransitions ?? new FsmTransition[0]) shape.GlobalTransitions.Add(t.EventName + ">" + t.ToState);
            return shape;
        }

        static string Path(PlayMakerFSM pm)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(pm.gameObject.name);
            for (UnityEngine.Transform p = pm.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.Append('#').Append(pm.FsmName).ToString();
        }

        /// <summary>The game version against the inventory, for the bug report and "mopr catalog".</summary>
        public static string GameLine()
        {
            string live = GameAssemblyHash();
            string match = live == null || InventoryGame == null ? "unknown"
                : string.Equals(live, InventoryGame, StringComparison.OrdinalIgnoreCase) ? "same game" : "DIFFERENT game (updated since this build)";
            return "Assembly-CSharp " + (live ?? "?") + ", inventory " + (InventoryGame ?? "?") + ": " + match;
        }

        public static string Status() =>
            (Ready ? "inventory loaded (" + fsmKeys.Count + " FSM structures)" : "inventory: " + Error) +
            ", checked " + Checked + ", vanilla " + Accepted + ", not vanilla " + Rejected + ", cached " + cache.Count;
    }
}
