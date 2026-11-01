// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// sleep.idle - script sleep, stage 1 (requirement 8, design §7; generalised from MOPR 5.x Next/Engine/BoltSleep.cs).
// An idle PlayMaker FSM still costs Unity two calls per frame (Update, LateUpdate). An FSM that provably does nothing
// is taken out of Unity's updates and PlayMaker's list - it is disabled with RestartOnEnable off, so it keeps its state -
// and woken before anything reaches it.
//
// When (§7.1): a vanilla FSM (inventory), the only PlayMakerFSM of its object, no proxy flags (Handle*: triggers,
// collisions, FixedUpdate, OnGUI, application events, mouse events: their proxies check Active and would drop the
// event), no sub-FSMs, no transition on a system event that Unity delivers as a message (became visible, level loaded,
// mouse...: a disabled component does not get them), and idle: started, entered, its state finished with no active
// action, no pending switch, no delayed events, every loop counter 0. Idle in the same state for 30 frames and 0.5 s
// (SleepPolicy; every state entry restarts the count), not restless (more than 5 wakes in 10 s ban it, doubling).
// Why this is the same as the game, by PlayMaker 1.6 (decompiled):
//   * PlayMakerFSM.Update / LateUpdate of such an FSM change nothing (FsmIdle, the fast path's proof: finished state).
//   * OnDisable: fsmList.Remove, then Fsm.Stop(): with RestartOnEnable false only Finished = true.
//     OnEnable: fsmList.Add, Fsm.OnEnable: Finished = false; ActiveState kept, no restart.
//   * Events: every path is hooked (below); variables are reached through GetComponents and work on a sleeper.
// How (FsmControl, design §7.2): through the ledger - FsmPause (RestartOnEnable = false) first, then FsmSleep
// (enabled = false). Giving back is LIFO: enabled first (OnEnable keeps the state), then RestartOnEnable. So the
// save barrier, the main switch and "restore" wake every sleeper on their own.
// Wake paths (each one wakes before the event is handled):
//   Fsm.ProcessEvent                      a sleeper with a transition for the event; without one the awake FSM would
//                                         only have copied eventData into the static EventData: done, the rest skipped
//   Fsm.BroadcastEvent, PlayMakerFSM.BroadcastEvent
//                                         sleepers with a transition, before the list is copied (one lookup otherwise)
//   Fsm.BroadcastEventToGameObject (+ children), Fsm.SendEventToFsmOnGameObject
//                                         sleepers on that object (and below) with a transition
//   PlayMakerFSM.FindFsmOnGameObject      the sleeper on that object, always (the lookup walks the list)
//   Fsm.FsmList (a copy of the list)      everything
//   EnableFSM (OnEnter, OnExit with reset) the sleeper on the target object, so the action switches an awake FSM
//   PlayMakerFSM.OnEnable of a sleeper    anyone else enabling it: the state is kept (as in the game, where it was
//                                         enabled all along), the restart flag comes back right after
//   the object switched off               a sentinel component's OnDisable does what the FSM's own OnDisable would
//                                         have done right then: restart flag back, Fsm.Stop() (reset with the flag)
// Order (requirement 8.4, OrderKeeper): PlayMakerFSM.OnEnable appends; a woken FSM is moved back to the place its
// stamp gives, every other OnEnable gets a new stamp. Unity's own update list cannot be restored from a mod: a woken
// FSM updates after the others from then on (§16, risk 1; the harness would show a difference it causes).
// Verify (§7.7, "virtual sleep"): nothing is disabled; the FSMs that would sleep are tracked as if asleep. A state
// change of one without a wake, or an event reaching one through a list without a wake, is a mismatch.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MOPR6.Catalog;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Core.Sleep;
using MOPR6.Diagnostics;
using MOPR6.Engine;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MOPR6.Sleep
{
    public sealed class FsmSleep : Optimization, Render.IAbSwitch
    {
        public static readonly FsmSleep Instance = new FsmSleep();
        public const string SystemId = "sleep.idle";
        const string HarmonyId = "mopr.sleep";
        const int CheckPerTick = 800;      // candidates looked at per host tick (0.25 s)
        const int SleepPerTick = 200;      // put to sleep per tick at most (each one is an OnDisable)
        const double CollectSeconds = 30;

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        internal sealed class Sleeper
        {
            public PlayMakerFSM Pm;
            public Fsm Fsm;
            public GameObject Go;
            public int Id;
            public FsmState State;
            public FsmEvent[] Events;
            public bool Virtual;
        }

        static readonly Dictionary<Fsm, Sleeper> sleepers = new Dictionary<Fsm, Sleeper>(2048, new RefComparer<Fsm>());
        // The sleepers of each object (an object may hold several FSMs; the targeted paths address the object).
        static readonly Dictionary<GameObject, List<Sleeper>> byObject = new Dictionary<GameObject, List<Sleeper>>(2048, new RefComparer<GameObject>());
        static readonly List<Sleeper> objectScratch = new List<Sleeper>(8);
        static readonly Dictionary<FsmEvent, int> reacting = new Dictionary<FsmEvent, int>(64, new RefComparer<FsmEvent>());
        static readonly List<PlayMakerFSM> candidates = new List<PlayMakerFSM>(4096);
        static readonly HashSet<PlayMakerFSM> candidateSet = new HashSet<PlayMakerFSM>(new RefComparer<PlayMakerFSM>());
        static readonly List<Sleeper> scratch = new List<Sleeper>(256);
        static readonly List<FsmEvent> eventScratch = new List<FsmEvent>(8);
        static readonly SleepPolicy policy = new SleepPolicy(new RefComparer<object>());
        static readonly OrderKeeper<PlayMakerFSM> order = new OrderKeeper<PlayMakerFSM>(new RefComparer<PlayMakerFSM>());

        static HarmonyInstance harmony;
        static bool verify, installedHooks;
        static MethodInfo setEventDataMethod;
        static readonly object[] setEventDataArgs = new object[1];
        static FieldInfo subFsmListField;
        static int cursor, listDepth;
        static double nextCollect;
        static string lastWakeAll = "-";
        static bool abHeld;                                  // A/B: A (awake), nothing is put to sleep
        static readonly List<Sleeper> parked = new List<Sleeper>(2048);   // woken by the A/B switch, slept again on B

        public static long Slept, WokenEvent, WokenBroadcast, WokenTargeted, WokenFind, WokenEnableFsm, WokenExternal,
                           WokenDeactivated, WokenAll, EventsKept, Restless, OrderFixed, OrderLost, OrderViolations, VerifyChecked, VerifyMismatches;
        static readonly Dictionary<string, long> mismatchKinds = new Dictionary<string, long>();
        static readonly Dictionary<string, int> skipReasons = new Dictionary<string, int>();

        FsmSleep() : base(SystemId) { }

        public override string Title => "Script sleep (idle FSMs)";
        public override Tier Tier => Tier.Sleep;
        // On by default: verify 0 mismatches (home, car, drive, Teimo), equivalence full 0 unexplained, steady windows
        // -1.8 ms (home) / -1.9 ms (car) median frame (2026-10-30; Docs/dormancy.ru.md).
        public override bool DefaultOn => true;
        public override long Mismatches => VerifyMismatches;

        // ----- Install / remove -------------------------------------------------------------------------------

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!FsmAccess.Init()) return "field access unavailable (" + FsmAccess.Error + ")";
            if (!Vanilla.Ready) return "no vanilla inventory (" + Vanilla.Error + ")";
            if (!FsmHooks.Installed) return "the FsmState.OnEnter hook is not installed";
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            setEventDataMethod = typeof(Fsm).GetMethod("SetEventDataSentByInfo", S, null, new[] { typeof(FsmEventData) }, null);
            subFsmListField = typeof(Fsm).GetField("subFsmList", I);
            MethodInfo processEvent = typeof(Fsm).GetMethod("ProcessEvent", I, null, new[] { typeof(FsmEvent), typeof(FsmEventData) }, null);
            MethodInfo broadcast = typeof(Fsm).GetMethod("BroadcastEvent", I, null, new[] { typeof(FsmEvent), typeof(bool) }, null);
            MethodInfo broadcastStatic = typeof(PlayMakerFSM).GetMethod("BroadcastEvent", S, null, new[] { typeof(FsmEvent) }, null);
            MethodInfo toGameObject = typeof(Fsm).GetMethod("BroadcastEventToGameObject", I, null,
                new[] { typeof(GameObject), typeof(FsmEvent), typeof(FsmEventData), typeof(bool), typeof(bool) }, null);
            MethodInfo toFsmOnGameObject = typeof(Fsm).GetMethod("SendEventToFsmOnGameObject", I, null,
                new[] { typeof(GameObject), typeof(string), typeof(FsmEvent) }, null);
            MethodInfo findFsm = typeof(PlayMakerFSM).GetMethod("FindFsmOnGameObject", S, null, new[] { typeof(GameObject), typeof(string) }, null);
            PropertyInfo fsmListProperty = typeof(Fsm).GetProperty("FsmList", S);
            MethodInfo fsmList = fsmListProperty != null ? fsmListProperty.GetGetMethod(true) : null;
            MethodInfo onEnable = typeof(PlayMakerFSM).GetMethod("OnEnable", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            MethodInfo efEnter = typeof(EnableFSM).GetMethod("OnEnter", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            MethodInfo efExit = typeof(EnableFSM).GetMethod("OnExit", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (setEventDataMethod == null || subFsmListField == null || processEvent == null || broadcast == null || broadcastStatic == null ||
                toGameObject == null || toFsmOnGameObject == null || findFsm == null || fsmList == null || onEnable == null || efEnter == null || efExit == null)
                return "PlayMaker methods not found";

            verify = mode == SystemMode.Verify;
            ResetCounters();
            order.Reset(PlayMakerFSM.FsmList);
            harmony = HarmonyInstance.Create(HarmonyId);
            harmony.Patch(processEvent, Hook(nameof(ProcessEventPrefix)), null, null);
            harmony.Patch(broadcast, Hook(nameof(BroadcastPrefix)), Hook(nameof(ListPostfix)), null);
            harmony.Patch(broadcastStatic, Hook(nameof(BroadcastPrefix)), Hook(nameof(ListPostfix)), null);
            harmony.Patch(toGameObject, Hook(nameof(ToGameObjectPrefix)), Hook(nameof(ListPostfix)), null);
            harmony.Patch(toFsmOnGameObject, Hook(nameof(ToFsmOnGameObjectPrefix)), Hook(nameof(ListPostfix)), null);
            harmony.Patch(findFsm, Hook(nameof(FindFsmPrefix)), null, null);
            harmony.Patch(fsmList, Hook(nameof(FsmListPrefix)), null, null);
            harmony.Patch(onEnable, Hook(nameof(OnEnablePrefix)), Hook(nameof(OnEnablePostfix)), null);
            harmony.Patch(efEnter, Hook(nameof(EnableFsmEnterPrefix)), null, null);
            harmony.Patch(efExit, Hook(nameof(EnableFsmExitPrefix)), null, null);
            installedHooks = true;
            candidates.Clear();
            candidateSet.Clear();
            nextCollect = 0;
            cursor = 0;
            return null;
        }

        protected internal override void OnRemove()
        {
            abHeld = false;
            parked.Clear();
            enabledByOthers.Clear();
            WakeAll("removed");
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            installedHooks = false;
            candidates.Clear();
            candidateSet.Clear();
        }

        protected internal override void OnSuspend(string reason)
        {
            parked.Clear();
            WakeAll(reason);
        }

        protected internal override void OnScene()
        {
            // The objects of the old scene are gone: nothing to wake. The list of the new scene gets new stamps.
            sleepers.Clear();
            byObject.Clear();
            reacting.Clear();
            candidates.Clear();
            candidateSet.Clear();
            policy.Clear();
            staticReasons.Clear();
            parked.Clear();
            enabledByOthers.Clear();
            abHeld = false;
            nextCollect = 0;
            cursor = 0;
            if (installedHooks) order.Reset(PlayMakerFSM.FsmList);
        }

        // Upkeep (task 43): Stopwatch ticks spent in the host tick (Collect included) and in the hooks when there are
        // sleepers (lookups and the wakes they cause). Harmony's own call into a hook is not counted (a few ns).
        static long tickTime, collectTime, collectMax, collects, hookTime, hookCalls;
        static int upkeepFrom;

        static void ResetCounters()
        {
            tickTime = collectTime = collectMax = collects = hookTime = hookCalls = 0;
            upkeepFrom = Time.frameCount;
            Slept = WokenEvent = WokenBroadcast = WokenTargeted = WokenFind = WokenEnableFsm = WokenExternal = WokenDeactivated =
                WokenAll = EventsKept = Restless = OrderFixed = OrderLost = OrderViolations = VerifyChecked = VerifyMismatches = AbLost = 0;
            mismatchKinds.Clear();
            skipReasons.Clear();
        }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(FsmSleep).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(FsmSleep), name);
            return new HarmonyMethod(m);
        }

        // ----- Tick: candidates and putting to sleep --------------------------------------------------------------

        protected internal override void OnTick(double now)
        {
            long t0 = Stopwatch.GetTimestamp();
            try { Tick(now); }
            finally { tickTime += Stopwatch.GetTimestamp() - t0; }
        }

        void Tick(double now)
        {
            listDepth = 0;   // the host ticks outside any broadcast (a broadcast that threw leaves no count behind)
            if (now >= nextCollect)
            {
                nextCollect = now + CollectSeconds;
                long c0 = Stopwatch.GetTimestamp();
                Collect();
                OrderViolations += order.Violations(PlayMakerFSM.FsmList);
                long c = Stopwatch.GetTimestamp() - c0;
                collectTime += c;
                if (c > collectMax) collectMax = c;
                collects++;
            }
            if (verify) CheckVirtual();
            if (abHeld) return;
            int frame = Time.frameCount;
            int budget = SleepPerTick;
            for (int n = 0; n < CheckPerTick && candidates.Count > 0 && budget > 0; n++)
            {
                if (cursor >= candidates.Count) cursor = 0;
                PlayMakerFSM pm = candidates[cursor++];
                if (pm == null) continue;
                Fsm fsm = pm.Fsm;
                if (fsm == null || sleepers.ContainsKey(fsm)) continue;
                if (!pm.enabled || !pm.gameObject.activeInHierarchy) { policy.Busy(fsm); continue; }
                FsmState state;
                if (!IsIdle(fsm, out state)) { policy.Busy(fsm); continue; }
                if (!policy.Idle(fsm, state, frame, now)) continue;
                if (HasMessageTransitions(fsm, state)) { Skip("message event"); policy.Busy(fsm); continue; }
                if (Sleep(pm, fsm, state)) budget--;
            }
        }

        static void Skip(string why)
        {
            int n;
            skipReasons.TryGetValue(why, out n);
            skipReasons[why] = n + 1;
        }

        /// <summary>Every FSM of the scene that may ever sleep (static conditions), from the active objects.</summary>
        void Collect()
        {
            candidates.Clear();
            candidateSet.Clear();
            skipReasons.Clear();
            int destroyed = order.Purge(p => p == null);
            if (destroyed > 0 || staticReasons.Count > 8192) PurgeReasons();
            foreach (PlayMakerFSM pm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (pm == null || pm.Fsm == null) continue;
                // The static conditions do not change while the component lives (the inventory, the flags and the
                // other components of the object are set when the scene loads); checked once per component.
                string why;
                if (!staticReasons.TryGetValue(pm, out why)) staticReasons[pm] = why = StaticReason(pm);
                if (why != null) { Skip(why); continue; }
                candidates.Add(pm);
                candidateSet.Add(pm);
            }
            // Sleepers are disabled: FindObjectsOfType does not return them; they stay candidates. Destroyed ones
            // (a part sold, a mod) only hold memory and their ledger entries.
            scratch.Clear();
            foreach (Sleeper s in sleepers.Values)
            {
                if (s.Pm == null) { scratch.Add(s); continue; }
                if (candidateSet.Add(s.Pm)) candidates.Add(s.Pm);
            }
            foreach (Sleeper s in scratch)
            {
                Forget(s);
                Arbiters.FsmSleep.Remove(s.Id, Id);
                Arbiters.FsmPause.Remove(s.Id, Id);
            }
            scratch.Clear();
            if (destroyed > 0) ModLog.Line(Id + ": " + destroyed + " destroyed FSMs forgotten");
        }

        static readonly Dictionary<PlayMakerFSM, string> staticReasons = new Dictionary<PlayMakerFSM, string>(4096, new RefComparer<PlayMakerFSM>());
        static readonly List<PlayMakerFSM> reasonScratch = new List<PlayMakerFSM>(256);

        static void PurgeReasons()
        {
            reasonScratch.Clear();
            foreach (PlayMakerFSM pm in staticReasons.Keys) if (pm == null) reasonScratch.Add(pm);
            for (int i = 0; i < reasonScratch.Count; i++) staticReasons.Remove(reasonScratch[i]);
            reasonScratch.Clear();
        }

        static string StaticReason(PlayMakerFSM pm)
        {
            Fsm f = pm.Fsm;
            if (!Vanilla.For(pm).Value) return "not vanilla";
            // Only the single FSM of its object. Tried for several (2026-10-31, the code below handles a list per
            // object): verify 0 mismatches, but after a save and a reload the harness found MaskedBearing2 (the masked
            // bolts of main bearing 2) left active with its bolt FSMs started, inactive in vanilla
            // (equiv-20261031-145314, eq-save-load). Not explained yet, and the measured gain was not clearly larger.
            if (pm.GetComponents<PlayMakerFSM>().Length != 1) return "several FSMs on the object";
            if (f.HandleTriggerEnter || f.HandleTriggerExit || f.HandleTriggerStay) return "triggers";
            if (f.HandleCollisionEnter || f.HandleCollisionExit || f.HandleCollisionStay) return "collisions";
            if (f.HandleOnGUI || f.HandleFixedUpdate || f.HandleApplicationEvents || f.MouseEvents) return "proxy events";
            System.Collections.ICollection sub = subFsmListField.GetValue(f) as System.Collections.ICollection;
            if (sub != null && sub.Count > 0) return "sub-FSMs";
            return null;
        }

        /// <summary>This FSM is asleep now (its component is disabled by the sleep, not by the game).</summary>
        internal static bool IsAsleep(Fsm fsm) => fsm != null && sleepers.Count > 0 && sleepers.ContainsKey(fsm);

        /// <summary>The hooks of the sleep are in place (world.deactivate wakes its parts through them).</summary>
        internal static bool HooksInstalled => installedHooks;

        /// <summary>
        /// Why an FSM can never be left alone (its proxies, sub-FSMs), null if nothing: the static conditions of §7.1
        /// apart from "the only FSM of its object" and the inventory.
        /// </summary>
        internal static string ProxyReason(Fsm f)
        {
            if (f.HandleTriggerEnter || f.HandleTriggerExit || f.HandleTriggerStay) return "FSM with triggers";
            if (f.HandleCollisionEnter || f.HandleCollisionExit || f.HandleCollisionStay) return "FSM with collisions";
            if (f.HandleOnGUI || f.HandleFixedUpdate || f.HandleApplicationEvents || f.MouseEvents) return "FSM with proxy events";
            System.Collections.ICollection sub = subFsmListField != null ? subFsmListField.GetValue(f) as System.Collections.ICollection : null;
            if (sub != null && sub.Count > 0) return "FSM with sub-FSMs";
            return null;
        }

        /// <summary>What an awake FSM does with an event it has no transition for: copies eventData into EventData.</summary>
        internal static void CopyEventData(FsmEvent fsmEvent, FsmEventData eventData)
        {
            if (FsmEvent.IsNullOrEmpty(fsmEvent) || eventData == null || setEventDataMethod == null) return;
            setEventDataArgs[0] = eventData;
            setEventDataMethod.Invoke(null, setEventDataArgs);
            setEventDataArgs[0] = null;
        }

        /// <summary>Provably idle (BoltSleep's test): started, entered, finished state without active actions, nothing pending.</summary>
        internal static bool IsIdle(Fsm fsm, out FsmState state)
        {
            state = null;
            if (!fsm.Started || fsm.Finished || Fsm.HitBreakpoint) return false;
            if (ReferenceEquals(FsmExecutionStack.ExecutingFsm, fsm)) return false;
            FsmState active, switchTo;
            int fl = FsmAccess.ReadFsm(fsm, out active, out switchTo);
            if ((fl & FsmAccess.EnteredBit) == 0 || active == null || switchTo != null) return false;
            Fsm owner;
            List<FsmStateAction> actions;
            int sl = FsmAccess.ReadState(active, out owner, out actions);
            if ((sl & FsmAccess.FinishedBit) == 0 || !ReferenceEquals(owner, fsm)) return false;
            if (actions != null && actions.Count > 0) return false;
            if (fsm.DelayedEvents.Count > 0) return false;
            FsmState[] states = fsm.States;
            if (states == null) return false;
            for (int i = 0; i < states.Length; i++)
                if (states[i] == null || states[i].loopCount != 0) return false;
            state = active;
            return true;
        }

        /// <summary>A transition (global or of the state) on a system event that Unity sends as a message (a disabled component misses it).</summary>
        internal static bool HasMessageTransitions(Fsm fsm, FsmState state)
        {
            return HasMessage(fsm.GlobalTransitions) || HasMessage(state.Transitions);
        }

        static bool HasMessage(FsmTransition[] transitions)
        {
            if (transitions == null) return false;
            for (int i = 0; i < transitions.Length; i++)
            {
                FsmEvent e = transitions[i] != null ? transitions[i].FsmEvent : null;
                if (e != null && e.IsSystemEvent && e.Name != "FINISHED") return true;
            }
            return false;
        }

        static FsmEvent[] EventsOf(Fsm fsm, FsmState state)
        {
            eventScratch.Clear();
            AddEvents(fsm.GlobalTransitions);
            AddEvents(state.Transitions);
            FsmEvent[] events = eventScratch.ToArray();
            eventScratch.Clear();
            return events;
        }

        static void AddEvents(FsmTransition[] transitions)
        {
            if (transitions == null) return;
            for (int i = 0; i < transitions.Length; i++)
            {
                FsmEvent e = transitions[i] != null ? transitions[i].FsmEvent : null;
                if (e != null && !eventScratch.Contains(e)) eventScratch.Add(e);
            }
        }

        bool Sleep(PlayMakerFSM pm, Fsm fsm, FsmState state, bool counted = true)
        {
            GameObject go = pm.gameObject;
            Sleeper s = new Sleeper { Pm = pm, Fsm = fsm, Go = go, Id = Arbiters.Id(pm), State = state, Events = EventsOf(fsm, state), Virtual = verify };
            if (!verify)
            {
                if (go.GetComponent<SleepSentinel>() == null) go.AddComponent<SleepSentinel>();
                // RestartOnEnable off first (OnDisable then keeps the state), then the component off.
                if (Arbiters.FsmPause.Add(s.Id, Id) != SetResult.Ok) return false;
                Add(s);
                if (Arbiters.FsmSleep.Add(s.Id, Id) != SetResult.Ok)
                {
                    Forget(s);
                    Arbiters.FsmPause.Remove(s.Id, Id);
                    return false;
                }
            }
            else Add(s);
            if (counted) Slept++;
            return true;
        }

        static void Add(Sleeper s)
        {
            sleepers[s.Fsm] = s;
            List<Sleeper> list;
            if (!byObject.TryGetValue(s.Go, out list)) byObject[s.Go] = list = new List<Sleeper>(2);
            list.Add(s);
            for (int i = 0; i < s.Events.Length; i++)
                reacting[s.Events[i]] = reacting.TryGetValue(s.Events[i], out int n) ? n + 1 : 1;
        }

        static bool Forget(Sleeper s)
        {
            if (!sleepers.Remove(s.Fsm)) return false;
            List<Sleeper> list;
            if (byObject.TryGetValue(s.Go, out list))
            {
                for (int i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], s)) { list.RemoveAt(i); break; }
                if (list.Count == 0) byObject.Remove(s.Go);
            }
            for (int i = 0; i < s.Events.Length; i++)
            {
                int n;
                if (!reacting.TryGetValue(s.Events[i], out n)) continue;
                if (n <= 1) reacting.Remove(s.Events[i]); else reacting[s.Events[i]] = n - 1;
            }
            return true;
        }

        static bool Reacts(Sleeper s, FsmEvent e)
        {
            if (FsmEvent.IsNullOrEmpty(e)) return false;
            for (int i = 0; i < s.Events.Length; i++)
                if (ReferenceEquals(s.Events[i], e)) return true;
            return false;
        }

        /// <summary>Wakes a sleeper: enabled (OnEnable keeps the state; the postfix puts it back in its place), then the restart flag.</summary>
        static void Wake(Sleeper s, bool counted = true)
        {
            if (!Forget(s)) return;
            if (counted && policy.Woken(s.Fsm, ModLog.Now())) Restless++;
            if (s.Virtual || s.Pm == null) return;
            pendingWake = s.Pm;    // the OnEnable postfix puts it back in its place
            try
            {
                Arbiters.FsmSleep.Remove(s.Id, Instance.Id);
                // The ledger lets an entry go without writing when the game took it over (EnableFSM was noted first):
                // then the FSM is enabled here, exactly as the ledger would have.
                if (!s.Pm.enabled && s.Go != null && s.Go.activeInHierarchy) s.Pm.enabled = true;
            }
            finally { pendingWake = null; }
            Arbiters.FsmPause.Remove(s.Id, Instance.Id);
        }

        /// <summary>From the FsmState.OnEnter hook: the idle stretch starts again; a sleeper entering a state was missed.</summary>
        public static void OnEnter(Fsm fsm)
        {
            if (!installedHooks || fsm == null) return;
            policy.Busy(fsm);
            if (sleepers.Count == 0) return;
            Sleeper s;
            if (!sleepers.TryGetValue(fsm, out s)) return;
            CountMismatch("state entered while asleep", s, fsm.ActiveStateName);
            if (s.Virtual) Forget(s);
            else Wake(s);
        }

        public static void WakeAll(string reason)
        {
            if (sleepers.Count == 0) return;
            scratch.Clear();
            scratch.AddRange(sleepers.Values);
            for (int i = 0; i < scratch.Count; i++)
            {
                try { Wake(scratch[i]); }
                catch (Exception ex) { ModLog.Report("sleep/WakeAll", ex, scratch[i].Go != null ? scratch[i].Go.name : null); }
            }
            WokenAll += scratch.Count;
            lastWakeAll = reason + " (" + scratch.Count + ")";
            scratch.Clear();
        }

        /// <summary>
        /// The sentinel: a sleeper's object is being switched off. The FSMs' own OnDisable would run now (they are
        /// disabled, so they do not): for each sleeper, in the order of the components, its restart flag back,
        /// Fsm.Stop() (reset with the flag, exit actions with it), the component enabled again without an OnEnable
        /// (the object is inactive) - from here on it is the game's.
        /// </summary>
        internal static void ObjectDisabled(GameObject go)
        {
            List<Sleeper> list;
            if (ReferenceEquals(go, null) || !byObject.TryGetValue(go, out list)) return;
            PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM pm = fsms[i];
                Sleeper s;
                if (pm == null || pm.Fsm == null || !sleepers.TryGetValue(pm.Fsm, out s) || s.Virtual) continue;
                try
                {
                    Forget(s);
                    WokenDeactivated++;
                    Arbiters.FsmPause.Remove(s.Id, Instance.Id);
                    s.Fsm.Stop();
                    Arbiters.FsmSleep.Remove(s.Id, Instance.Id);
                    if (!s.Pm.enabled) s.Pm.enabled = true;
                }
                catch (Exception ex) { Instance.Fail("ObjectDisabled", ex, go.name); }
            }
        }

        // ----- Verify: the would-be sleepers keep running -----------------------------------------------------------

        void CheckVirtual()
        {
            if (sleepers.Count == 0) return;
            scratch.Clear();
            foreach (Sleeper s in sleepers.Values) scratch.Add(s);
            foreach (Sleeper s in scratch)
            {
                VerifyChecked++;
                if (s.Pm == null || !s.Pm.enabled || !s.Go.activeInHierarchy) { Forget(s); continue; }
                FsmState state;
                if (!IsIdle(s.Fsm, out state) || !ReferenceEquals(state, s.State))
                    Mismatch("changed while asleep", s, s.Fsm.ActiveStateName);
            }
            scratch.Clear();
        }

        /// <summary>A claim of sleep that turned out wrong (verify, or a sleeper that changed): counted, logged, forgotten.</summary>
        static void Mismatch(string kind, Sleeper s, string detail)
        {
            CountMismatch(kind, s, detail);
            Forget(s);
        }

        static void CountMismatch(string kind, Sleeper s, string detail)
        {
            VerifyMismatches++;
            long n;
            mismatchKinds.TryGetValue(kind, out n);
            mismatchKinds[kind] = n + 1;
            if (n < 5) ModLog.Line("sleep: " + kind + ": " + Path(s.Go) + "#" + (s.Pm != null ? s.Pm.FsmName : "?") + " " + detail);
        }

        static string Path(GameObject go)
        {
            if (go == null) return "?";
            StringBuilder sb = new StringBuilder(go.name);
            for (Transform p = go.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>
        /// Verify: the method that called ProcessEvent (the first frame that is not ProcessEvent itself or this class).
        /// Harmony's replacements are dynamic methods named "Name_Patch1": the suffix is cut.
        /// </summary>
        static string Caller()
        {
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0; i < st.FrameCount; i++)
            {
                MethodBase m = st.GetFrame(i).GetMethod();
                if (m == null || m.DeclaringType == typeof(FsmSleep)) continue;
                string n = m.Name;
                int patch = n.IndexOf("_Patch", StringComparison.Ordinal);
                if (patch > 0) n = n.Substring(0, patch);
                if (n == "ProcessEvent") continue;
                return n;
            }
            return "?";
        }

        /// <summary>
        /// Callers that reach one FSM directly (not through PlayMaker's list): the ProcessEvent prefix itself is their
        /// wake (Fsm.Event with Self / FSMComponent targets, PlayMakerFSM.SendEvent, delayed events, Unity messages).
        /// </summary>
        static readonly HashSet<string> directCallers = new HashSet<string>
        {
            "Event", "SendEvent", "Start", "UpdateDelayedEvents", "Update", "OnApplicationQuit", "OnBecameVisible", "OnBecameInvisible",
            "OnLevelWasLoaded", "OnControllerColliderHit"
        };

        // ----- Patches ----------------------------------------------------------------------------------------

        // Every hook below serves two kinds of "asleep": the FSMs of this system and the FSMs of the parts of the world
        // switched off by world.deactivate (World.DormantIndex, design §9.2). A part is woken whole, before the event.
        static bool ProcessEventPrefix(Fsm __instance, FsmEvent fsmEvent, FsmEventData eventData)
        {
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return true;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // An FSM of a part switched off, without a transition for the event: the awake FSM would only have
                // copied eventData, as for a sleeper.
                if (World.DormantIndex.Units > 0 && World.DormantIndex.Event(__instance, fsmEvent))
                {
                    CopyEventData(fsmEvent, eventData);
                    return false;
                }
                if (sleepers.Count == 0) return true;
                return ProcessEvent(__instance, fsmEvent, eventData);
            }
            finally { HookDone(t0); }
        }

        static void HookDone(long t0)
        {
            hookTime += Stopwatch.GetTimestamp() - t0;
            hookCalls++;
        }

        static bool ProcessEvent(Fsm fsm, FsmEvent fsmEvent, FsmEventData eventData)
        {
            Sleeper s;
            if (!sleepers.TryGetValue(fsm, out s)) return true;
            try
            {
                if (s.Virtual)
                {
                    if (!Reacts(s, fsmEvent)) return true;
                    if (listDepth > 0) { Mismatch("event through a list without a wake", s, fsmEvent.Name); return true; }
                    string caller = Caller();
                    if (!directCallers.Contains(caller)) { Mismatch("unknown event path " + caller, s, fsmEvent.Name); return true; }
                    Wake(s);
                    WokenEvent++;
                    return true;
                }
                if (Reacts(s, fsmEvent))
                {
                    Wake(s);
                    WokenEvent++;
                    return true;
                }
                // The awake FSM would only have copied eventData into the static EventData (no vanilla FSM reads it,
                // but it costs nothing to do the same), then found no transition.
                if (!FsmEvent.IsNullOrEmpty(fsmEvent) && eventData != null)
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
                Instance.Fail("ProcessEvent", ex, s.Go != null ? s.Go.name : null);
                try { Wake(s); } catch (Exception ex2) { ModLog.Report("sleep/ProcessEvent.wake", ex2); }
                return true;
            }
        }

        static void BroadcastPrefix(FsmEvent fsmEvent)
        {
            listDepth++;
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (World.DormantIndex.Units > 0) World.DormantIndex.Broadcast(fsmEvent);
                if (sleepers.Count == 0 || fsmEvent == null || !reacting.ContainsKey(fsmEvent)) return;
                scratch.Clear();
                foreach (Sleeper s in sleepers.Values)
                    if (Reacts(s, fsmEvent)) scratch.Add(s);
                for (int i = 0; i < scratch.Count; i++) { Wake(scratch[i]); WokenBroadcast++; }
                scratch.Clear();
            }
            catch (Exception ex)
            {
                Instance.Fail("Broadcast", ex);
                WakeAll("broadcast error");
            }
            finally { HookDone(t0); }
        }

        static void ListPostfix() { if (listDepth > 0) listDepth--; }

        static void WakeOnObject(GameObject go, string fsmName, FsmEvent e, bool always, bool children)
        {
            if (ReferenceEquals(go, null)) return;
            List<Sleeper> list;
            if (byObject.TryGetValue(go, out list))
            {
                // A copy: every wake removes its sleeper from the list.
                objectScratch.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    Sleeper s = list[i];
                    if ((string.IsNullOrEmpty(fsmName) || (s.Pm != null && s.Pm.FsmName == fsmName)) && (always || Reacts(s, e))) objectScratch.Add(s);
                }
                for (int i = 0; i < objectScratch.Count; i++)
                {
                    Wake(objectScratch[i]);
                    if (always) WokenFind++; else WokenTargeted++;
                }
                objectScratch.Clear();
            }
            if (!children || go == null) return;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++) WakeOnObject(t.GetChild(i).gameObject, fsmName, e, always, true);
        }

        static void ToGameObjectPrefix(GameObject go, FsmEvent fsmEvent, FsmEventData eventData, bool sendToChildren)
        {
            listDepth++;
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (World.DormantIndex.Units > 0) World.DormantIndex.ToGameObject(go, fsmEvent, sendToChildren);
                if (sleepers.Count > 0 && fsmEvent != null && reacting.ContainsKey(fsmEvent)) WakeOnObject(go, null, fsmEvent, false, sendToChildren);
            }
            catch (Exception ex) { Instance.Fail("ToGameObject", ex); WakeAll("targeted error"); }
            finally { HookDone(t0); }
        }

        static void ToFsmOnGameObjectPrefix(GameObject gameObject, string fsmName, FsmEvent fsmEvent)
        {
            listDepth++;
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (World.DormantIndex.Units > 0) World.DormantIndex.ToGameObject(gameObject, fsmEvent, false);
                if (sleepers.Count > 0 && fsmEvent != null && reacting.ContainsKey(fsmEvent)) WakeOnObject(gameObject, fsmName, fsmEvent, false, false);
            }
            catch (Exception ex) { Instance.Fail("ToFsmOnGameObject", ex); WakeAll("targeted error"); }
            finally { HookDone(t0); }
        }

        static void FindFsmPrefix(GameObject go, string fsmName)
        {
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (World.DormantIndex.Units > 0) World.DormantIndex.Lookup(go, "FindFsmOnGameObject");
                if (sleepers.Count > 0) WakeOnObject(go, fsmName, null, true, false);
            }
            catch (Exception ex) { Instance.Fail("FindFsm", ex); WakeAll("find error"); }
            finally { HookDone(t0); }
        }

        static void FsmListPrefix()
        {
            if (World.DormantIndex.Units > 0) World.DormantIndex.WakeAll("Fsm.FsmList");
            if (sleepers.Count > 0) WakeAll("Fsm.FsmList");
        }

        // A sleeper somebody else enables, from the OnEnable prefix to its postfix. Not Harmony's __state: Harmony 1.x
        // declares that local only for prefixes, and UnpatchAll (prefix first) then fails to rebuild the postfix alone.
        // Keyed by the component: OnEnable may enable other FSMs inside (actions of a restarted FSM).
        static readonly Dictionary<PlayMakerFSM, Sleeper> enabledByOthers = new Dictionary<PlayMakerFSM, Sleeper>(new RefComparer<PlayMakerFSM>());

        /// <summary>
        /// Before OnEnable. A wake (ours, or a sleeper somebody else enables - the ledger too) keeps its stamp and is
        /// put back in the postfix. Any other FSM is stamped here, when OnEnable appends it: a restarted FSM's actions
        /// may enable more FSMs inside its OnEnable, and those come after it in the list.
        /// </summary>
        static void OnEnablePrefix(PlayMakerFSM __instance)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (enabledByOthers.Count > 0) enabledByOthers.Remove(__instance);   // left over by an OnEnable that threw
            try
            {
                if (ReferenceEquals(pendingWake, __instance)) return;
                Fsm fsm = __instance.Fsm;
                Sleeper s;
                if (sleepers.Count > 0 && fsm != null && sleepers.TryGetValue(fsm, out s) && !s.Virtual)
                {
                    // Somebody else enables it (our wakes forget first): awake from now on, its state was kept.
                    Forget(s);
                    WokenExternal++;
                    enabledByOthers[__instance] = s;
                    return;
                }
                // The ledger gives an object back (a part of the world switched on again, RestoreAll): in the game the
                // FSM never left the list; the postfix puts it back at its stamp.
                if (Arbiters.Restoring > 0) return;
                order.Enabled(__instance);
            }
            catch (Exception ex) { Instance.Fail("OnEnable", ex); }
            finally { HookDone(t0); }
        }

        /// <summary>
        /// After OnEnable: a woken FSM (appended last; its OnEnable runs no actions, the state is kept) goes back to
        /// its place. A sleeper somebody else enabled gets its restart flag back right after this OnEnable.
        /// </summary>
        static void OnEnablePostfix(PlayMakerFSM __instance)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Sleeper external = null;
                if (enabledByOthers.Count > 0 && enabledByOthers.TryGetValue(__instance, out external))
                {
                    enabledByOthers.Remove(__instance);
                    Arbiters.FsmSleep.Remove(external.Id, Instance.Id);
                    Arbiters.FsmPause.Remove(external.Id, Instance.Id);
                }
                else if (!ReferenceEquals(pendingWake, __instance) && Arbiters.Restoring == 0) return;   // stamped in the prefix
                if (order.Restore(PlayMakerFSM.FsmList, __instance) >= 0) OrderFixed++;
                else
                {
                    // Not last or never stamped: it stays where OnEnable put it, and its stamp says so.
                    order.Enabled(__instance);
                    OrderLost++;
                }
            }
            catch (Exception ex) { Instance.Fail("OnEnable.postfix", ex); }
            finally { HookDone(t0); }
        }

        static PlayMakerFSM pendingWake;

        /// <summary>EnableFSM on a sleeper's object: woken first, so the action switches an awake FSM as in the game.</summary>
        static void EnableFsmEnterPrefix(EnableFSM __instance)
        {
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            try
            {
                if (World.DormantIndex.Units > 0 && __instance.Fsm != null) World.DormantIndex.Lookup(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject), "EnableFSM");
                if (sleepers.Count > 0) WakeEnableFsmTarget(__instance);
            }
            catch (Exception ex) { Instance.Fail("EnableFSM", ex); WakeAll("EnableFSM error"); }
        }

        static void EnableFsmExitPrefix(EnableFSM __instance)
        {
            if (sleepers.Count == 0 && World.DormantIndex.Units == 0) return;
            try
            {
                if (__instance.resetOnExit == null || !__instance.resetOnExit.Value) return;
                if (World.DormantIndex.Units > 0 && __instance.Fsm != null) World.DormantIndex.Lookup(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject), "EnableFSM");
                if (sleepers.Count > 0) WakeEnableFsmTarget(__instance);
            }
            catch (Exception ex) { Instance.Fail("EnableFSM.exit", ex); WakeAll("EnableFSM error"); }
        }

        static void WakeEnableFsmTarget(EnableFSM a)
        {
            if (a.Fsm == null || a.gameObject == null) return;
            GameObject go = a.Fsm.GetOwnerDefaultTarget(a.gameObject);
            List<Sleeper> list;
            if (go == null || !byObject.TryGetValue(go, out list)) return;
            // Every sleeper of the object: the action picks its FSM by name, or the first component without one.
            objectScratch.Clear();
            objectScratch.AddRange(list);
            for (int i = 0; i < objectScratch.Count; i++)
            {
                Wake(objectScratch[i]);
                WokenEnableFsm++;
            }
            objectScratch.Clear();
        }

        // ----- A/B (TestBot) -----------------------------------------------------------------------------------

        /// <summary>
        /// A/B without a reinstall: off = every sleeper woken (not counted as a wake, no restless ban), nothing is put
        /// to sleep; on = the same FSMs asleep again at once, those still idle in the same state.
        /// </summary>
        public string SetAb(bool on)
        {
            if (!installedHooks) return "not installed";
            if (verify) return "verify mode";
            long hooksBefore = hookTime, callsBefore = hookCalls;   // the switch's own wakes are not upkeep
            try { return SwitchAb(on); }
            finally { hookTime = hooksBefore; hookCalls = callsBefore; }
        }

        string SwitchAb(bool on)
        {
            if (!on)
            {
                if (abHeld) return null;
                abHeld = true;
                parked.Clear();
                parked.AddRange(sleepers.Values);
                for (int i = 0; i < parked.Count; i++)
                {
                    try { Wake(parked[i], false); }
                    catch (Exception ex) { ModLog.Report("sleep/ab", ex); }
                }
                return null;
            }
            if (!abHeld) return null;
            abHeld = false;
            int again = 0;
            for (int i = 0; i < parked.Count; i++)
            {
                Sleeper s = parked[i];
                if (s.Pm == null || s.Fsm == null || s.Go == null || sleepers.ContainsKey(s.Fsm)) continue;
                if (!s.Pm.enabled || !s.Go.activeInHierarchy) continue;
                FsmState state;
                if (!IsIdle(s.Fsm, out state) || !ReferenceEquals(state, s.State)) continue;
                if (Sleep(s.Pm, s.Fsm, state, false)) again++;
            }
            AbLost += parked.Count - again;
            parked.Clear();
            return null;
        }

        static long AbLost;   // A/B: woken on A and not asleep again on B (they did something meanwhile)

        // ----- Report -------------------------------------------------------------------------------------------

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(sleepers.Count).Append(verify ? " virtually asleep" : " asleep").Append(" of ").Append(candidates.Count)
              .Append(" candidates, PlayMaker list ").Append(PlayMakerFSM.FsmList.Count).Append("; put to sleep ").Append(Slept)
              .Append("; woken by an event ").Append(WokenEvent).Append(", a broadcast ").Append(WokenBroadcast).Append(", a targeted send ").Append(WokenTargeted)
              .Append(", a lookup ").Append(WokenFind).Append(", EnableFSM ").Append(WokenEnableFsm).Append(", someone else ").Append(WokenExternal)
              .Append(", object switched off ").Append(WokenDeactivated).Append(", all at once ").Append(WokenAll).Append(" (last: ").Append(lastWakeAll)
              .Append("); events that changed nothing ").Append(EventsKept).Append(", restless bans ").Append(Restless)
              .Append("; order: put back ").Append(OrderFixed).Append(", not put back ").Append(OrderLost)
              .Append(", out of order ").Append(installedHooks ? order.Violations(PlayMakerFSM.FsmList) : 0).Append(" now, ")
              .Append(OrderViolations).Append(" in all checks");
            if (AbLost > 0) sb.Append("; A/B: not asleep again ").Append(AbLost);
            sb.Append('\n');
            int frames = Math.Max(1, Time.frameCount - upkeepFrom);
            double us = 1e6 / Stopwatch.Frequency;
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("  upkeep over ").Append(frames).Append(" frames: tick ").Append((tickTime * us / frames).ToString("0.0", inv))
              .Append(" us/frame (collect ").Append(collects).Append(" x ").Append((collects > 0 ? collectTime * us / 1000 / collects : 0).ToString("0.0", inv))
              .Append(" ms, max ").Append((collectMax * us / 1000).ToString("0.0", inv)).Append(" ms), hooks ")
              .Append((hookTime * us / frames).ToString("0.0", inv)).Append(" us/frame (")
              .Append(((double)hookCalls / frames).ToString("0.0", inv)).Append(" calls/frame)\n");
            sb.Append("  not candidates: ");
            foreach (KeyValuePair<string, int> kv in skipReasons) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append(", ");
            sb.Append('\n');
            if (verify)
            {
                sb.Append("  verify: checked ").Append(VerifyChecked).Append(", mismatches ").Append(VerifyMismatches);
                foreach (KeyValuePair<string, long> kv in mismatchKinds) sb.Append("; ").Append(kv.Key).Append(' ').Append(kv.Value);
                sb.Append('\n');
            }
        }
    }

    /// <summary>
    /// On a sleeping FSM's object: when the object is switched off, does what the FSM's own OnDisable would have done
    /// (FsmSleep.ObjectDisabled). No Update, no cost otherwise. Kept on the object afterwards.
    /// </summary>
    public sealed class SleepSentinel : MonoBehaviour
    {
        void OnDisable() { FsmSleep.ObjectDisabled(gameObject); }
    }
}
