// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Sleeping bolts (5.1.1, experimental, off by default): the safe part of the MOPR 4.x "Satsuma driving mode".
//
// What 4.x did and what it cost (TestBot drive-modes / drive-modes2, seated in the Satsuma in Peraejaervi):
// switching about 100 objects off with SetActive (install triggers, the pivots of the engine parts with the parts on
// them, the MiscParts parts) saved 1.01 ms per frame (+20% FPS). The engine pivots were half of it: their renderers
// 0.18 ms (0.45 driving) and their FSMs 0.36 ms; physics nothing (all 107 rigidbodies there are kinematic). But a
// switched-off object also stops its game logic and misses every event: a loose engine part could no longer break off
// at high RPM, tool mode (REPAIRMODE_ON/OFF) and the save event (SAVEGAME) went past those parts, the headlight and
// rear light pivots took the lights with them, and every FSM restarted from its first state when it came back.
//
// What this does instead: only the per-bolt "Screw" FSMs (objects named BoltPM that have no other FSM), and only while
// they are provably idle, are paused - and they are woken the moment anything addresses them. A Screw FSM spends its
// life in a state whose actions have all finished ("Idle", or "State 1"/"State 2" after the tool mode switched its
// collider): such a state does nothing per frame, so PlayMaker's Update and LateUpdate change nothing. Still, Unity
// calls both for every enabled FSM: 248 bolts on the test car cost 0.43 ms per frame with MOPR's fast path, 1.26 ms
// without (drive-modes3). A paused FSM is a disabled PlayMakerFSM with RestartOnEnable off (so disabling keeps its
// state, exactly as RestartGuard pauses FSMs). PlayMaker drops every event to a disabled FSM, so every way an event
// reaches an FSM is hooked (verified against the decompiled PlayMaker 1.6 of the game):
//   Fsm.ProcessEvent                     every event ends here: a sleeper that has a transition for the event is
//                                        woken first, then the original runs. Without one, the original would only
//                                        set the static EventData from eventData: that is done, the rest is skipped.
//   Fsm.BroadcastEvent, PlayMakerFSM.BroadcastEvent
//                                        iterate PlayMakerFSM.FsmList (enabled FSMs only): sleepers that would react
//                                        are woken before the list is copied.
//   Fsm.BroadcastEventToGameObject, Fsm.SendEventToFsmOnGameObject, PlayMakerFSM.FindFsmOnGameObject
//                                        look FSMs up in that list by object: the sleeper on that object is woken
//                                        (by a lookup: always; by an event: if it has a transition for it).
//   Fsm.FsmList                          a copy of that list: everything is woken.
//   EnableFSM (action, Assembly-CSharp)  finds its FSM with GetComponents, a sleeper too: woken before the action
//                                        switches it (the carburettor's Removal FSM disables its bolt's Screw FSM).
//   Variables (GetFsmInt, SetFsmFloat...) are reached through GetComponents and work on a sleeper as they are.
// Waking = enabling the component (OnEnable: back in the list, the state kept), then RestartOnEnable back. When the
// bolt's object is switched off while its FSM sleeps (a part comes off, MOP or a mod hides it), a sentinel component
// on the object wakes it when the object comes back - with RestartOnEnable already restored, so it restarts exactly as
// it would have (in the game it would have been reset when switched off and restarted when switched on; the reset of
// a state without running actions has no other effect); woken while its object is still off (the player gets out), it
// is stopped the way that OnDisable would have stopped it. Someone else enabling a sleeper (PlayMakerFSM.OnEnable hook)
// gets the state kept during that OnEnable and its restart flag back right after it - or the restart, if its object
// was switched off meanwhile.
//
// Cost of the hooks: the sleepers are indexed by FSM and by object, and a broadcast is rejected by one lookup unless
// some sleeper has a transition for that event (tool mode); nothing runs per frame for a sleeper.
//
// Scope: only while the player sits in a vehicle (where nobody works on bolts), only while MOPR is active; before every
// save, when the player gets out, the setting goes off or MOPR stops, everything is woken.
//
// Not identical to the game (and why it does not matter here): a woken FSM is appended to PlayMakerFSM.FsmList, so it
// gets broadcast events after the FSMs that were in the list before; Unity may update it in another order among the
// FSMs (only FSMs of different objects: a bolt object with another FSM is never paused). The loop counters of a
// sleeper are not reset every frame - it only sleeps when all of them are 0 already. Code that walks
// PlayMakerFSM.FsmList by itself (no mod known) does not see a sleeping bolt.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

using MOP.Common;
using MOP.FSM;

namespace MOP.Next.Engine
{
    static class BoltSleep
    {
        const string HarmonyId = "com.anickon.mopr.engine.boltsleep";
        const string FsmName = "Screw";
        const string ObjectName = "BoltPM";

        const float SleepInterval = 0.5f;      // s between two rounds of putting idle bolts to sleep
        const int SleepBudget = 128;           // bolts put to sleep per frame at most (each one is an OnDisable)
        const float CollectAge = 30f;          // s: the list of bolts is collected again when entering after that

        internal sealed class Sleeper   // internal: Harmony's generated method holds one as __state
        {
            public PlayMakerFSM Pm;
            public Fsm Fsm;
            public GameObject Go;
            public BoltSleepSentinel Sentinel;
            public bool Restart;
            public FsmEvent[] Events;          // the events it has a transition for (global + its state); fixed while it sleeps
        }

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<Fsm, Sleeper> sleepers = new Dictionary<Fsm, Sleeper>(512, new RefComparer<Fsm>());
        static readonly Dictionary<GameObject, Sleeper> byObject = new Dictionary<GameObject, Sleeper>(512, new RefComparer<GameObject>());
        // FsmEvent has no equality of its own: PlayMaker compares transitions by reference, so does this.
        static readonly Dictionary<FsmEvent, int> reacting = new Dictionary<FsmEvent, int>(16, new RefComparer<FsmEvent>());
        static readonly List<Sleeper> scratch = new List<Sleeper>(512);
        static readonly List<FsmEvent> eventScratch = new List<FsmEvent>(8);
        static readonly List<PlayMakerFSM> candidates = new List<PlayMakerFSM>(1024);
        static float collectedAt = -1000f, nextRound;
        static int cursor;
        static bool collected;

        static HarmonyInstance harmony;
        static FieldInfo delayedEventsField, efGameObject, efResetOnExit;
        static MethodInfo setEventDataMethod;
        static readonly object[] setEventDataArgs = new object[1];

        public static bool Installed => harmony != null;
        public static string Status { get; private set; } = "not installed";

        /// <summary>A/B measurement only (TestBot): while true, nothing is put to sleep (sleepers are woken).</summary>
        public static bool Bypass { get; set; }

        public static int Asleep => sleepers.Count;
        public static long Slept, WokenEvent, WokenBroadcast, WokenTargeted, WokenReactivated, WokenExternal, WokenAll, EventsKept;
        static string lastWakeAll = "-";

        // ----- Install / remove -------------------------------------------------------------------------

        public static string Install()
        {
            Remove();
            try
            {
                if (!FsmAccess.Init()) return "field access unavailable (" + FsmAccess.Error + ")";
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                delayedEventsField = typeof(Fsm).GetField("delayedEvents", I);
                setEventDataMethod = typeof(Fsm).GetMethod("SetEventDataSentByInfo", S, null, new[] { typeof(FsmEventData) }, null);
                MethodInfo processEvent = typeof(Fsm).GetMethod("ProcessEvent", I, null, new[] { typeof(FsmEvent), typeof(FsmEventData) }, null);
                MethodInfo broadcast = typeof(Fsm).GetMethod("BroadcastEvent", I, null, new[] { typeof(FsmEvent), typeof(bool) }, null);
                MethodInfo broadcastStatic = typeof(PlayMakerFSM).GetMethod("BroadcastEvent", S, null, new[] { typeof(FsmEvent) }, null);
                MethodInfo toGameObject = typeof(Fsm).GetMethod("BroadcastEventToGameObject", I, null,
                    new[] { typeof(GameObject), typeof(FsmEvent), typeof(FsmEventData), typeof(bool), typeof(bool) }, null);
                MethodInfo toFsmOnGameObject = typeof(Fsm).GetMethod("SendEventToFsmOnGameObject", I, null,
                    new[] { typeof(GameObject), typeof(string), typeof(FsmEvent) }, null);
                MethodInfo findFsm = typeof(PlayMakerFSM).GetMethod("FindFsmOnGameObject", S, null, new[] { typeof(GameObject), typeof(string) }, null);
                MethodInfo fsmList = typeof(Fsm).GetProperty("FsmList", S)?.GetGetMethod(true);
                MethodInfo onEnable = typeof(PlayMakerFSM).GetMethod("OnEnable", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

                // The game's EnableFSM action finds its FSM with GetComponents (a sleeper too) and sets enabled.
                Type enableFsm = FindType("HutongGames.PlayMaker.Actions.EnableFSM");
                MethodInfo efEnter = enableFsm?.GetMethod("OnEnter", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                MethodInfo efExit = enableFsm?.GetMethod("OnExit", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                efGameObject = enableFsm?.GetField("gameObject", I);
                efResetOnExit = enableFsm?.GetField("resetOnExit", I);

                if (delayedEventsField == null || !typeof(System.Collections.ICollection).IsAssignableFrom(delayedEventsField.FieldType) ||
                    setEventDataMethod == null || processEvent == null || broadcast == null || broadcastStatic == null ||
                    toGameObject == null || toFsmOnGameObject == null || findFsm == null || fsmList == null || onEnable == null)
                    return "PlayMaker methods not found";
                if (efEnter == null || efExit == null || efGameObject == null || efGameObject.FieldType != typeof(FsmOwnerDefault) ||
                    efResetOnExit == null || efResetOnExit.FieldType != typeof(FsmBool))
                    return "EnableFSM action not found";

                harmony = HarmonyInstance.Create(HarmonyId);
                harmony.Patch(processEvent, Hook(nameof(ProcessEventPrefix)), null, null);
                harmony.Patch(broadcast, Hook(nameof(BroadcastPrefix)), null, null);
                harmony.Patch(broadcastStatic, Hook(nameof(BroadcastPrefix)), null, null);
                harmony.Patch(toGameObject, Hook(nameof(ToGameObjectPrefix)), null, null);
                harmony.Patch(toFsmOnGameObject, Hook(nameof(ToFsmOnGameObjectPrefix)), null, null);
                harmony.Patch(findFsm, Hook(nameof(FindFsmPrefix)), null, null);
                harmony.Patch(fsmList, Hook(nameof(FsmListPrefix)), null, null);
                harmony.Patch(onEnable, Hook(nameof(OnEnablePrefix)), Hook(nameof(OnEnablePostfix)), null);
                harmony.Patch(efEnter, Hook(nameof(EnableFsmEnterPrefix)), null, null);
                harmony.Patch(efExit, Hook(nameof(EnableFsmExitPrefix)), null, null);
                Status = "installed";
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                Status = "failed";
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Remove()
        {
            WakeAll("removed");
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            candidates.Clear();
            collected = false;
            Status = "not installed";
        }

        public static void ResetStats()
        {
            Slept = WokenEvent = WokenBroadcast = WokenTargeted = WokenReactivated = WokenExternal = WokenAll = EventsKept = 0;
        }

        /// <summary>Scene change: the objects are gone, nothing to wake.</summary>
        public static void ClearCaches()
        {
            sleepers.Clear();
            byObject.Clear();
            reacting.Clear();
            candidates.Clear();
            collected = false;
            collectedAt = -1000f;
        }

        static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(BoltSleep).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(fullName, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        // ----- Per frame (Hypervisor.Update) ------------------------------------------------------------

        public static void Tick()
        {
            if (harmony == null) return;
            bool wanted = !Bypass && MopSettings.IsModActive && FsmManager.IsPlayerInCar();
            if (!wanted)
            {
                if (sleepers.Count > 0) WakeAll(Bypass ? "bypass" : !MopSettings.IsModActive ? "MOPR inactive" : "player out of the vehicle");
                collected = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (!collected)
            {
                if (now - collectedAt > CollectAge || candidates.Count == 0) Collect();
                collected = true;
                cursor = 0;
                nextRound = now;
            }
            if (now < nextRound) return;

            int budget = SleepBudget;
            while (cursor < candidates.Count && budget > 0)
            {
                PlayMakerFSM pm = candidates[cursor++];
                if (pm == null) continue;
                if (TrySleep(pm)) budget--;
            }
            if (cursor >= candidates.Count)
            {
                cursor = 0;
                nextRound = now + SleepInterval;
                PurgeDestroyed();
            }
        }

        /// <summary>Bolt FSMs of active objects, on objects that have no other FSM.</summary>
        static void Collect()
        {
            candidates.Clear();
            foreach (PlayMakerFSM pm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (pm == null || pm.FsmName != FsmName || pm.gameObject.name != ObjectName) continue;
                if (pm.GetComponents<PlayMakerFSM>().Length != 1) continue;
                candidates.Add(pm);
            }
            collectedAt = Time.realtimeSinceStartup;
        }

        /// <summary>Sleepers whose FSM was destroyed (a part sold, a mod) only hold memory: forget them.</summary>
        static void PurgeDestroyed()
        {
            if (sleepers.Count == 0) return;
            scratch.Clear();
            foreach (Sleeper s in sleepers.Values)
                if (s.Pm == null) scratch.Add(s);
            for (int i = 0; i < scratch.Count; i++) Forget(scratch[i]);
            scratch.Clear();
        }

        // ----- Sleep / wake -----------------------------------------------------------------------------

        static bool TrySleep(PlayMakerFSM pm)
        {
            if (!pm.enabled) return false;
            GameObject go = pm.gameObject;
            if (!go.activeInHierarchy || byObject.ContainsKey(go)) return false;
            Fsm fsm = pm.Fsm;
            if (fsm == null || sleepers.ContainsKey(fsm) || !IsIdle(fsm)) return false;

            BoltSleepSentinel sentinel = go.GetComponent<BoltSleepSentinel>();
            if (sentinel == null) sentinel = go.AddComponent<BoltSleepSentinel>();
            sentinel.Fsm = pm;
            Sleeper s = new Sleeper { Pm = pm, Fsm = fsm, Go = go, Sentinel = sentinel, Restart = fsm.RestartOnEnable, Events = EventsOf(fsm) };

            Add(s);
            fsm.RestartOnEnable = false;   // OnDisable keeps the state (Stop() resets only with RestartOnEnable)
            pm.enabled = false;
            Slept++;
            return true;
        }

        /// <summary>Provably idle: started, entered, its state finished with no running action, nothing pending.</summary>
        static bool IsIdle(Fsm fsm)
        {
            if (!fsm.Started || fsm.Finished || Fsm.HitBreakpoint) return false;
            if (ReferenceEquals(FsmExecutionStack.ExecutingFsm, fsm)) return false;
            int fl = FsmAccess.ReadFsm(fsm, out FsmState active, out FsmState switchTo);
            if ((fl & FsmAccess.EnteredBit) == 0 || active == null || switchTo != null) return false;
            int sl = FsmAccess.ReadState(active, out Fsm owner, out List<FsmStateAction> actions);
            if ((sl & FsmAccess.FinishedBit) == 0 || !ReferenceEquals(owner, fsm)) return false;
            if (actions != null && actions.Count > 0) return false;
            if (delayedEventsField.GetValue(fsm) is System.Collections.ICollection delayed && delayed.Count > 0) return false;
            FsmState[] states = fsm.States;
            if (states == null) return false;
            for (int i = 0; i < states.Length; i++)
                if (states[i] == null || states[i].loopCount != 0) return false;
            return true;
        }

        /// <summary>What ProcessEvent would look at for this FSM in its current state: global and state transitions.</summary>
        static FsmEvent[] EventsOf(Fsm fsm)
        {
            eventScratch.Clear();
            AddEvents(fsm.GlobalTransitions);
            AddEvents(fsm.ActiveState?.Transitions);
            FsmEvent[] events = eventScratch.ToArray();
            eventScratch.Clear();
            return events;
        }

        static void AddEvents(FsmTransition[] transitions)
        {
            if (transitions == null) return;
            for (int i = 0; i < transitions.Length; i++)
            {
                FsmEvent e = transitions[i]?.FsmEvent;
                if (e != null && !eventScratch.Contains(e)) eventScratch.Add(e);
            }
        }

        static void Add(Sleeper s)
        {
            sleepers[s.Fsm] = s;
            byObject[s.Go] = s;
            for (int i = 0; i < s.Events.Length; i++)
                reacting[s.Events[i]] = reacting.TryGetValue(s.Events[i], out int n) ? n + 1 : 1;
        }

        static void Forget(Sleeper s)
        {
            if (!sleepers.Remove(s.Fsm)) return;
            if (byObject.TryGetValue(s.Go, out Sleeper o) && ReferenceEquals(o, s)) byObject.Remove(s.Go);
            for (int i = 0; i < s.Events.Length; i++)
            {
                if (!reacting.TryGetValue(s.Events[i], out int n)) continue;
                if (n <= 1) reacting.Remove(s.Events[i]); else reacting[s.Events[i]] = n - 1;
            }
        }

        static bool Reacts(Sleeper s, FsmEvent e)
        {
            if (FsmEvent.IsNullOrEmpty(e)) return false;
            FsmEvent[] events = s.Events;
            for (int i = 0; i < events.Length; i++)
                if (ReferenceEquals(events[i], e)) return true;
            return false;
        }

        static void Wake(Sleeper s, bool reactivated)
        {
            Forget(s);
            if (s.Pm == null) return;
            if (reactivated)
            {
                // The object was switched off and on: restart as the game would have.
                s.Fsm.RestartOnEnable = s.Restart;
                s.Pm.enabled = true;
            }
            else if (s.Go != null && !s.Go.activeInHierarchy)
            {
                // Its object was switched off while it slept (it only sleeps on an active object): the game's
                // OnDisable would have stopped it then - with its restart flag, so reset to no state. Done now; the
                // component is enabled without an OnEnable (inactive object) and starts when the object comes back.
                s.Fsm.RestartOnEnable = s.Restart;
                s.Fsm.Stop();
                s.Pm.enabled = true;
            }
            else
            {
                // OnEnable keeps the state (RestartOnEnable still off), then the restart flag goes back.
                s.Pm.enabled = true;
                s.Fsm.RestartOnEnable = s.Restart;
            }
        }

        public static void WakeAll(string reason)
        {
            if (sleepers.Count == 0) return;
            scratch.Clear();
            scratch.AddRange(sleepers.Values);
            for (int i = 0; i < scratch.Count; i++)
            {
                try { Wake(scratch[i], false); }
                catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_WAKE"); }
            }
            WokenAll += scratch.Count;
            lastWakeAll = reason + " (" + scratch.Count + ")";
            scratch.Clear();
        }

        /// <summary>Sentinel: the bolt's object came back after being switched off while its FSM slept.</summary>
        internal static void OnReactivated(PlayMakerFSM pm)
        {
            if (pm == null || pm.Fsm == null || !sleepers.TryGetValue(pm.Fsm, out Sleeper s)) return;
            try
            {
                Wake(s, true);
                WokenReactivated++;
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_REACTIVATED"); }
        }

        // ----- Patches ----------------------------------------------------------------------------------

        static bool ProcessEventPrefix(Fsm __instance, FsmEvent fsmEvent, FsmEventData eventData)
        {
            if (sleepers.Count == 0 || !sleepers.TryGetValue(__instance, out Sleeper s)) return true;
            try
            {
                if (Reacts(s, fsmEvent))
                {
                    Wake(s, false);
                    WokenEvent++;
                    return true;
                }
                // The awake FSM would only have copied eventData into the static EventData, then found nothing to do
                // (on a switched-off object it would not even have done that).
                if (!FsmEvent.IsNullOrEmpty(fsmEvent) && eventData != null && s.Go != null && s.Go.activeInHierarchy)
                {
                    setEventDataArgs[0] = eventData;
                    setEventDataMethod.Invoke(null, setEventDataArgs);
                    setEventDataArgs[0] = null;
                }
                EventsKept++;
                return false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "BOLT_SLEEP_EVENT");
                try { Wake(s, false); } catch { }
                return true;
            }
        }

        static void BroadcastPrefix(FsmEvent fsmEvent)
        {
            if (sleepers.Count == 0 || fsmEvent == null || !reacting.ContainsKey(fsmEvent)) return;
            try
            {
                scratch.Clear();
                foreach (Sleeper s in sleepers.Values)
                    if (Reacts(s, fsmEvent)) scratch.Add(s);
                for (int i = 0; i < scratch.Count; i++) { Wake(scratch[i], false); WokenBroadcast++; }
                scratch.Clear();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "BOLT_SLEEP_BROADCAST");
                WakeAll("broadcast error");
            }
        }

        static void WakeOnObject(GameObject go, string fsmName, FsmEvent e, bool always)
        {
            if (ReferenceEquals(go, null) || !byObject.TryGetValue(go, out Sleeper s)) return;
            if (!string.IsNullOrEmpty(fsmName) && (s.Pm == null || s.Pm.FsmName != fsmName)) return;
            if (!always && !Reacts(s, e)) return;
            Wake(s, false);
            WokenTargeted++;
        }

        static void ToGameObjectPrefix(GameObject go, FsmEvent fsmEvent)
        {
            if (sleepers.Count == 0) return;
            try { WakeOnObject(go, null, fsmEvent, false); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_TARGETED"); WakeAll("targeted error"); }
        }

        static void ToFsmOnGameObjectPrefix(GameObject gameObject, string fsmName, FsmEvent fsmEvent)
        {
            if (sleepers.Count == 0) return;
            try { WakeOnObject(gameObject, fsmName, fsmEvent, false); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_TARGETED"); WakeAll("targeted error"); }
        }

        static void FindFsmPrefix(GameObject go, string fsmName)
        {
            if (sleepers.Count == 0) return;
            try { WakeOnObject(go, fsmName, null, true); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_FIND"); WakeAll("find error"); }
        }

        static void FsmListPrefix()
        {
            if (sleepers.Count > 0) WakeAll("Fsm.FsmList");
        }

        /// <summary>
        /// Someone else enables a sleeping FSM (our own wakes forget it first): it is awake from now on. Its object
        /// was switched off meanwhile - restart as the game would have; else the state is kept for this OnEnable
        /// (as in the game, where it was enabled all along) and the restart flag goes back right after it.
        /// </summary>
        static void OnEnablePrefix(PlayMakerFSM __instance, out Sleeper __state)
        {
            __state = null;
            if (sleepers.Count == 0) return;
            try
            {
                Fsm fsm = __instance.Fsm;
                if (fsm == null || !sleepers.TryGetValue(fsm, out Sleeper s)) return;
                Forget(s);
                WokenExternal++;
                if (s.Sentinel != null && s.Sentinel.WasDisabled) fsm.RestartOnEnable = s.Restart;
                else __state = s;
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_ENABLE"); }
        }

        static void OnEnablePostfix(Sleeper __state)
        {
            if (__state != null) __state.Fsm.RestartOnEnable = __state.Restart;
        }

        /// <summary>
        /// EnableFSM on a sleeper's object: woken first, so the action switches an awake FSM as in the game - enabling
        /// changes nothing, disabling stops it with its real restart flag (it would otherwise be enabled again on the
        /// next wake). The carburettor's Removal FSM does this to its bolt when the part is removed.
        /// </summary>
        static void EnableFsmEnterPrefix(FsmStateAction __instance)
        {
            if (sleepers.Count == 0) return;
            try { WakeEnableFsmTarget(__instance); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_ENABLEFSM"); WakeAll("EnableFSM error"); }
        }

        static void EnableFsmExitPrefix(FsmStateAction __instance)
        {
            if (sleepers.Count == 0) return;
            try
            {
                if (efResetOnExit.GetValue(__instance) is FsmBool reset && reset.Value) WakeEnableFsmTarget(__instance);
            }
            catch (Exception ex) { ExceptionManager.New(ex, false, "BOLT_SLEEP_ENABLEFSM"); WakeAll("EnableFSM error"); }
        }

        static void WakeEnableFsmTarget(FsmStateAction action)
        {
            if (action.Fsm == null || !(efGameObject.GetValue(action) is FsmOwnerDefault owner)) return;
            // A sleeper is the only FSM of its object: whatever FSM name the action asks for, it gets this one or none.
            WakeOnObject(action.Fsm.GetOwnerDefaultTarget(owner), null, null, true);
        }

        // ----- Report -----------------------------------------------------------------------------------

        public static string Report()
        {
            if (harmony == null) return "Sleeping bolts while driving (experimental): " + (Status == "not installed" ? "off" : Status);
            return $"Sleeping bolts while driving (experimental): ON{(Bypass ? " (bypassed)" : "")}, {sleepers.Count} asleep now of {candidates.Count} bolts, " +
                   $"put to sleep {Slept} times; woken by an event {WokenEvent}, a broadcast {WokenBroadcast}, a targeted send {WokenTargeted}, " +
                   $"their object coming back {WokenReactivated}, someone else {WokenExternal}, all at once {WokenAll} (last: {lastWakeAll}); " +
                   $"events that changed nothing {EventsKept}";
        }
    }

    /// <summary>
    /// On a sleeping bolt's object: when the object is switched off and on again, the FSM is woken with its restart, as
    /// the game would have restarted it. No Update, no cost otherwise. Copies of the object (Instantiate) keep nothing.
    /// </summary>
    internal class BoltSleepSentinel : MonoBehaviour
    {
        [NonSerialized] internal PlayMakerFSM Fsm;
        [NonSerialized] bool wasDisabled;

        /// <summary>The object was switched off since this component was last enabled.</summary>
        internal bool WasDisabled => wasDisabled;

        void OnDisable() => wasDisabled = true;

        void OnEnable()
        {
            if (!wasDisabled) return;
            wasDisabled = false;
            BoltSleep.OnReactivated(Fsm);
        }
    }
}
