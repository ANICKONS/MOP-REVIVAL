// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The FSMs and objects of the parts of the world switched off now (world.deactivate, design §9.2), and every way the
// game can reach them. Each path wakes the whole part BEFORE the game's call goes on, so the call meets the part as it
// is in the game, where it never went off:
//   Fsm.ProcessEvent, Fsm/PlayMakerFSM.BroadcastEvent, BroadcastEventToGameObject, SendEventToFsmOnGameObject
//                          an FSM of the part with a transition for the event (the hooks of the script sleep call in)
//   PlayMakerFSM.FindFsmOnGameObject, EnableFSM, Fsm.FsmList
//                          always (the lookups walk PlayMaker's list, where the part's FSMs are not)
//   Fsm.GetOwnerDefaultTarget (own prefix)
//                          an action outside the part aims at an object inside it: ActivateGameObject, SetParent,
//                          animations, sounds, SendMessage, EnableBehaviour... Only reading actions (Get*, comparisons)
//                          and the FSM variable writers (SetFsm*: the part's FSMs are idle, a variable write starts
//                          nothing in PlayMaker) and SendEvent* (the event hooks decide) go on without a wake
//   FindGameObject (own prefix)
//                          a name or a tag that is in the part (GameObject.Find does not see inactive objects)
//   FsmState.OnEnter       an FSM of the part entering a state was reached by a path nobody hooked: a mismatch, the
//                          part is woken (in verify the parts run: that is how missed paths are found)
// Every entry point returns at once while no part is off (one static read).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MOPR6.Diagnostics;
using UnityEngine;

namespace MOPR6.World
{
    public static class DormantIndex
    {
        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        sealed class FsmEntry
        {
            public Dormancy.Unit Unit;
            public FsmEvent[] Events;
        }

        /// <summary>Parts registered now (switched off, or virtually in verify).</summary>
        public static int Units;
        static readonly List<Dormancy.Unit> units = new List<Dormancy.Unit>();
        static readonly Dictionary<Fsm, FsmEntry> fsms = new Dictionary<Fsm, FsmEntry>(2048, new RefComparer<Fsm>());
        static readonly Dictionary<GameObject, Dormancy.Unit> objects = new Dictionary<GameObject, Dormancy.Unit>(8192, new RefComparer<GameObject>());
        static readonly Dictionary<FsmEvent, int> reacting = new Dictionary<FsmEvent, int>(256, new RefComparer<FsmEvent>());
        static readonly Dictionary<string, int> names = new Dictionary<string, int>(4096, StringComparer.Ordinal);
        static readonly Dictionary<string, int> tags = new Dictionary<string, int>(64, StringComparer.Ordinal);
        static readonly List<Dormancy.Unit> scratch = new List<Dormancy.Unit>(32);
        // The last state entry of every FSM (the quiet test of the cover: no entry for 10 s).
        static readonly Dictionary<Fsm, double> lastEnter = new Dictionary<Fsm, double>(8192, new RefComparer<Fsm>());
        static readonly Dictionary<Type, bool> harmless = new Dictionary<Type, bool>();

        public static long Mismatches, TargetHits, TargetHarmless;
        static readonly Dictionary<string, long> mismatchKinds = new Dictionary<string, long>();
        public static IDictionary<string, long> MismatchKinds => mismatchKinds;

        // ----- Registration (Dormancy) ------------------------------------------------------------------------

        internal static void Register(Dormancy.Unit u)
        {
            if (units.Contains(u)) return;
            units.Add(u);
            Units = units.Count;
            for (int i = 0; i < u.IndexFsms.Count; i++)
            {
                KeyValuePair<Fsm, FsmEvent[]> kv = u.IndexFsms[i];
                fsms[kv.Key] = new FsmEntry { Unit = u, Events = kv.Value };
                for (int e = 0; e < kv.Value.Length; e++) reacting[kv.Value[e]] = reacting.TryGetValue(kv.Value[e], out int n) ? n + 1 : 1;
            }
            for (int i = 0; i < u.Objects.Count; i++) if (!ReferenceEquals(u.Objects[i], null)) objects[u.Objects[i]] = u;
            foreach (string n in u.Names) names[n] = names.TryGetValue(n, out int c) ? c + 1 : 1;
            foreach (string t in u.Tags) tags[t] = tags.TryGetValue(t, out int c) ? c + 1 : 1;
        }

        internal static void Unregister(Dormancy.Unit u)
        {
            if (!units.Remove(u)) return;
            Units = units.Count;
            for (int i = 0; i < u.IndexFsms.Count; i++)
            {
                KeyValuePair<Fsm, FsmEvent[]> kv = u.IndexFsms[i];
                FsmEntry f;
                if (!fsms.TryGetValue(kv.Key, out f) || !ReferenceEquals(f.Unit, u)) continue;
                fsms.Remove(kv.Key);
                for (int e = 0; e < f.Events.Length; e++) Decrement(reacting, f.Events[e]);
            }
            for (int i = 0; i < u.Objects.Count; i++)
            {
                GameObject go = u.Objects[i];
                Dormancy.Unit owner;
                if (!ReferenceEquals(go, null) && objects.TryGetValue(go, out owner) && ReferenceEquals(owner, u)) objects.Remove(go);
            }
            foreach (string n in u.Names) Decrement(names, n);
            foreach (string t in u.Tags) Decrement(tags, t);
        }

        static void Decrement<T>(Dictionary<T, int> d, T key)
        {
            int n;
            if (!d.TryGetValue(key, out n)) return;
            if (n <= 1) d.Remove(key); else d[key] = n - 1;
        }

        /// <summary>A new scene: the old objects are gone.</summary>
        internal static void Clear()
        {
            units.Clear();
            Units = 0;
            fsms.Clear();
            objects.Clear();
            reacting.Clear();
            names.Clear();
            tags.Clear();
            lastEnter.Clear();
        }

        internal static void ResetCounters()
        {
            Mismatches = TargetHits = TargetHarmless = 0;
            mismatchKinds.Clear();
        }

        /// <summary>The FSM entered a state within the last <paramref name="seconds"/>.</summary>
        internal static bool EnteredWithin(Fsm fsm, double now, double seconds)
        {
            double t;
            return lastEnter.TryGetValue(fsm, out t) && now - t < seconds;
        }

        internal static bool Holds(GameObject go) => !ReferenceEquals(go, null) && objects.ContainsKey(go);

        // ----- Wakes ------------------------------------------------------------------------------------------

        static void Wake(Dormancy.Unit u, string why)
        {
            try { Dormancy.Instance.WakeFromHook(u, why); }
            catch (Exception ex) { Dormancy.Instance.Fail("Wake", ex, u.Name + ": " + why); }
        }

        static bool Reacts(FsmEvent[] events, FsmEvent e)
        {
            if (FsmEvent.IsNullOrEmpty(e)) return false;
            for (int i = 0; i < events.Length; i++) if (ReferenceEquals(events[i], e)) return true;
            return false;
        }

        /// <summary>
        /// Fsm.ProcessEvent: true when the FSM is in a part switched off and has no transition for the event (the
        /// caller then does what the awake FSM would have done: nothing but EventData). A transition wakes the part.
        /// </summary>
        public static bool Event(Fsm fsm, FsmEvent e)
        {
            FsmEntry f;
            if (fsm == null || !fsms.TryGetValue(fsm, out f)) return false;
            if (!Reacts(f.Events, e)) return !f.Unit.Virtual;   // verify: the FSM runs, so does the event
            Wake(f.Unit, "event " + e.Name);
            return false;
        }

        /// <summary>A broadcast to every FSM: the parts with an FSM that has a transition for it.</summary>
        public static void Broadcast(FsmEvent e)
        {
            if (e == null || !reacting.ContainsKey(e)) return;
            scratch.Clear();
            for (int i = 0; i < units.Count; i++) if (units[i].Events.Contains(e)) scratch.Add(units[i]);
            for (int i = 0; i < scratch.Count; i++) Wake(scratch[i], "broadcast " + e.Name);
            scratch.Clear();
        }

        /// <summary>An event to the FSMs of one object (and its children).</summary>
        public static void ToGameObject(GameObject go, FsmEvent e, bool children)
        {
            if (ReferenceEquals(go, null) || e == null || !reacting.ContainsKey(e)) return;
            Dormancy.Unit u;
            if (objects.TryGetValue(go, out u))
            {
                if (u.Events.Contains(e)) Wake(u, "event " + e.Name + " to " + go.name);
                return;
            }
            if (!children || go == null) return;
            // Sent to an ancestor of a part with its children.
            scratch.Clear();
            Transform t = go.transform;
            for (int i = 0; i < units.Count; i++)
                if (units[i].Events.Contains(e) && units[i].CoverUnder(t)) scratch.Add(units[i]);
            for (int i = 0; i < scratch.Count; i++) Wake(scratch[i], "event " + e.Name + " to " + go.name + " and children");
            scratch.Clear();
        }

        /// <summary>A lookup or a switch that walks PlayMaker's list or needs the object awake: always a wake.</summary>
        public static void Lookup(GameObject go, string why)
        {
            Dormancy.Unit u;
            if (!ReferenceEquals(go, null) && objects.TryGetValue(go, out u)) Wake(u, why);
        }

        public static void WakeAll(string why)
        {
            scratch.Clear();
            scratch.AddRange(units);
            for (int i = 0; i < scratch.Count; i++) Wake(scratch[i], why);
            scratch.Clear();
        }

        /// <summary>world.deactivate is installed: state entries are recorded (the quiet test needs the last 10 s).</summary>
        internal static bool Tracking;

        /// <summary>From the FsmState.OnEnter hook (every state entry of the game).</summary>
        public static void StateEntered(Fsm fsm)
        {
            if (!Tracking || fsm == null) return;
            lastEnter[fsm] = ModLog.Now();
            if (Units == 0) return;
            FsmEntry f;
            if (!fsms.TryGetValue(fsm, out f)) return;
            Mismatch("state entered while off", f.Unit, fsm.Name + "/" + fsm.ActiveStateName);
            Wake(f.Unit, "state entered (missed path)");
        }

        static void Mismatch(string kind, Dormancy.Unit u, string detail)
        {
            Mismatches++;
            long n;
            mismatchKinds.TryGetValue(kind, out n);
            mismatchKinds[kind] = n + 1;
            if (n < 5) ModLog.Line(Dormancy.SystemId + ": " + kind + ": " + u.Name + " " + detail);
        }

        // ----- Own prefixes (installed by Dormancy) --------------------------------------------------------------

        /// <summary>Fsm.GetOwnerDefaultTarget: an action of the game aims at an object.</summary>
        internal static void OwnerTargetPrefix(Fsm __instance, FsmOwnerDefault ownerDefault)
        {
            if (Units == 0 || ownerDefault == null || ownerDefault.OwnerOption == OwnerDefaultOption.UseOwner) return;
            try
            {
                FsmGameObject g = ownerDefault.GameObject;
                GameObject go = g != null ? g.Value : null;
                Dormancy.Unit u;
                if (ReferenceEquals(go, null) || !objects.TryGetValue(go, out u)) return;
                TargetHits++;
                if (__instance != null && fsms.ContainsKey(__instance)) return;   // the part's own FSM (it runs only in verify)
                FsmStateAction a = FsmExecutionStack.ExecutingAction;
                if (a != null && Harmless(a.GetType())) { TargetHarmless++; return; }
                Wake(u, "action " + (a != null ? a.GetType().Name : "(none)") + " on " + go.name);
            }
            catch (Exception ex) { Dormancy.Instance.Fail("GetOwnerDefaultTarget", ex); }
        }

        /// <summary>
        /// Actions that may aim at an object of a part switched off without waking it: they read (the values of an
        /// inactive object are the same), write FSM variables of idle FSMs, or send events (the event hooks decide).
        /// </summary>
        static bool Harmless(Type t)
        {
            bool h;
            if (harmless.TryGetValue(t, out h)) return h;
            string n = t.Name;
            // ArrayMaker's readers (ArrayListGet*, HashTableGet*, counts, lookups) read the lists of a proxy component,
            // which keep their data on an inactive object (the Gifu's ShitDumps: ArrayListGetClosestGameObject).
            h = t.Namespace == "HutongGames.PlayMaker.Actions" &&
                (n.StartsWith("Get", StringComparison.Ordinal) || n.StartsWith("SetFsm", StringComparison.Ordinal) ||
                 n.StartsWith("SendEvent", StringComparison.Ordinal) || n == "GameObjectCompare" || n == "GameObjectCompareTag" ||
                 n == "GameObjectIsChildOf" || n == "GameObjectHasChildren" || n == "GameObjectIsNull" || n == "FindChild" ||
                 n.StartsWith("ArrayListGet", StringComparison.Ordinal) || n.StartsWith("HashTableGet", StringComparison.Ordinal) ||
                 n == "ArrayListCount" || n == "HashTableCount" || n == "ArrayListContains" || n == "HashTableContains" ||
                 n == "HashTableContainsKey" || n == "HashTableContainsValue" || n == "ArrayListIndexOf" || n == "ArrayListLastIndexOf");
            harmless[t] = h;
            return h;
        }

        /// <summary>FindGameObject.OnEnter: GameObject.Find / FindWithTag do not see inactive objects.</summary>
        internal static void FindGameObjectPrefix(FindGameObject __instance)
        {
            if (Units == 0) return;
            try
            {
                string tag = __instance.withTag != null ? __instance.withTag.Value : null;
                if (!string.IsNullOrEmpty(tag) && tag != "Untagged")
                {
                    if (tags.ContainsKey(tag)) WakeWhere(null, tag, "FindGameObject tag " + tag);
                    return;
                }
                string name = __instance.objectName != null ? __instance.objectName.Value : null;
                if (string.IsNullOrEmpty(name)) return;
                int slash = name.LastIndexOf('/');
                string leaf = slash >= 0 ? name.Substring(slash + 1) : name;
                if (names.ContainsKey(leaf)) WakeWhere(leaf, null, "FindGameObject " + name);
            }
            catch (Exception ex) { Dormancy.Instance.Fail("FindGameObject", ex); }
        }

        static void WakeWhere(string name, string tag, string why)
        {
            scratch.Clear();
            for (int i = 0; i < units.Count; i++)
                if ((name != null && units[i].Names.Contains(name)) || (tag != null && units[i].Tags.Contains(tag))) scratch.Add(units[i]);
            for (int i = 0; i < scratch.Count; i++) Wake(scratch[i], why);
            scratch.Clear();
        }
    }
}
