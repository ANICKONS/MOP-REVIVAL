// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// PlayMaker idle fast path.
//
// Every enabled PlayMakerFSM runs Update() and LateUpdate() each frame, even when its active state has nothing
// left to do (all actions finished, waiting for an event). PlayMaker 1.6 still pushes the FSM on the execution
// stack, copies the delayed event list, calls Init() on every active action (two native Unity calls each),
// and resets the loop counter of EVERY state of the FSM - twice per frame, for every FSM.
//
// This module skips those calls ONLY when it can prove, from the live PlayMaker state, that the original
// method would not change anything. The proof follows the decompiled PlayMaker 1.6 code line by line:
//
//   PlayMakerFSM.Update -> Fsm.Update:
//     FsmTime.RealtimeBugFix()                 -> we call it too (sets a static flag)
//     owner == null -> return                  -> we require fsm.Owner to be this live component
//     FsmExecutionStack.Push/Pop               -> balanced; MaxStackCount would not grow (checked)
//     if (!activeStateEntered) Continue()      -> required: entered
//     UpdateDelayedEvents()                    -> required: no delayed events
//     UpdateState(ActiveState):
//       state.Fsm = this                       -> required: state.fsm is already this FSM
//       state.OnUpdate():
//         if finished -> nothing               -> SKIP
//         else StateTime += deltaTime          -> we do exactly this (same IL)
//              foreach active action: Init(state); OnUpdate()
//                                              -> required: the action type overrides neither Init nor
//                                                 OnUpdate, and Init would write the values it already has
//                                                 (5.1: or it is an "evaluated" action that would send nothing,
//                                                 see "Evaluated actions" below)
//              CheckAllActionsFinished()       -> no-op while actions are active (or the state is inactive)
//       UpdateStateChanges():
//         while (switchToState != null) ...    -> required: no pending state switch
//         foreach state: ResetLoopCount()      -> required: every loopCount is already 0 (see "clean" below)
//
//   PlayMakerFSM.LateUpdate:
//     FsmVariables.GlobalVariablesSynced = false   -> we do it too
//     Fsm.LateUpdate: same reasoning with OnLateUpdate (no StateTime, CheckAllActionsFinished must be a no-op).
//
// "Clean" FSMs: loopCount is only incremented by FsmState.OnEnter and only cleared by the reset loop above.
// A prefix on FsmState.OnEnter removes the FSM from the clean set before the counter changes; an FSM is put
// (back) into the set only after all its loop counters were read as 0. The States array reference is stored
// as well, so replacing the array invalidates the entry.
//
// Anything unexpected -> the original method runs. In SHADOW mode the original always runs and the claimed
// "no change" is verified afterwards; every mismatch is counted and logged.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOP.Next.Engine
{
    static class FsmFastPath
    {
        // ----- Classification codes -------------------------------------------------------------------
        // >= 0: run the original (reason). < 0: provably no-op.
        public const int RunBreakpoint = 0;
        public const int RunNotEntered = 1;
        public const int RunDelayedEvents = 2;
        public const int RunSwitching = 3;
        public const int RunOwner = 4;
        public const int RunStack = 5;
        public const int RunNoActiveState = 6;
        public const int RunStateFsm = 7;
        public const int RunWillFinish = 8;
        public const int RunNullAction = 9;
        public const int RunActionOverride = 10;
        public const int RunActionInit = 11;
        public const int RunLoopCount = 12;
        public const int RunEvaluatedActive = 13;   // 5.1: an evaluated action would send an event this frame
        public const int ReasonCount = 14;

        public const int SkipIdle = -1;      // state finished / nothing to update
        public const int SkipTimer = -2;     // only StateTime advances

        public static readonly string[] ReasonNames =
        {
            "breakpoint", "not-entered", "delayed-events", "switching", "owner", "stack", "no-active-state",
            "state-fsm", "will-finish", "null-action", "action-override", "action-init", "loop-count",
            "evaluated-active"
        };

        // ----- Statistics -----------------------------------------------------------------------------
        public sealed class Counters
        {
            public long Calls, SkipIdle, SkipTimer, SkipEvaluated, ShadowChecked, ShadowMismatch;
            public readonly long[] Reasons = new long[ReasonCount];
            public void Reset()
            {
                Calls = SkipIdle = SkipTimer = SkipEvaluated = ShadowChecked = ShadowMismatch = 0;
                Array.Clear(Reasons, 0, Reasons.Length);
            }
        }

        public static readonly Counters UpdateStats = new Counters();
        public static readonly Counters LateStats = new Counters();
        public static bool DetailedStats;
        public static readonly Dictionary<Type, long> UpdateBlockers = new Dictionary<Type, long>();
        public static readonly Dictionary<Type, long> LateBlockers = new Dictionary<Type, long>();
        public static readonly List<string> MismatchLog = new List<string>();          // distinct mismatches, first seen first
        public static readonly Dictionary<string, long> MismatchCounts = new Dictionary<string, long>();

        // ----- State ----------------------------------------------------------------------------------
        public static bool Active { get; private set; }
        public static bool Shadow { get; private set; }

        const string HarmonyId = "com.anickon.mopr.engine";
        static HarmonyInstance harmony;

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<Fsm, FsmState[]> clean = new Dictionary<Fsm, FsmState[]>(4096, new RefComparer<Fsm>());
        const int CleanLimit = 30000;

        const int FlagUpdate = 1, FlagLate = 2, FlagInit = 4;
        static readonly Dictionary<Type, int> typeFlags = new Dictionary<Type, int>();
        static Type lastType;
        static int lastFlags;

        // ----- Install / remove -----------------------------------------------------------------------

        public static string Install(bool shadow)
        {
            Remove();
            if (!FsmAccess.Init())
                return "field access unavailable (" + FsmAccess.Error + ")";

            MethodInfo pmUpdate = typeof(PlayMakerFSM).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            MethodInfo pmLate = typeof(PlayMakerFSM).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            MethodInfo stateEnter = typeof(FsmState).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (pmUpdate == null || pmLate == null || stateEnter == null)
                return "PlayMaker methods not found";

            clean.Clear();
            Shadow = shadow;
            try
            {
                harmony = HarmonyInstance.Create(HarmonyId);
                // The OnEnter hook first: the clean set must never miss a loop counter change.
                harmony.Patch(stateEnter, Hook("OnEnterPrefix"), null, null);
                if (shadow)
                {
                    // Every Fsm.Event overload ends in Event(FsmEventTarget, FsmEvent): watch it for claimed no-ops.
                    MethodInfo fsmEvent = null;
                    foreach (MethodInfo m in typeof(Fsm).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "Event") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType.Name == "FsmEventTarget" && ps[1].ParameterType == typeof(FsmEvent)) { fsmEvent = m; break; }
                    }
                    if (fsmEvent == null) throw new MissingMethodException("Fsm", "Event(FsmEventTarget, FsmEvent)");
                    harmony.Patch(fsmEvent, Hook("EventPrefixShadow"), null, null);
                    harmony.Patch(pmUpdate, Hook("UpdatePrefixShadow"), Hook("UpdatePostfixShadow"), null);
                    harmony.Patch(pmLate, Hook("LatePrefixShadow"), Hook("LatePostfixShadow"), null);
                }
                else
                {
                    harmony.Patch(pmUpdate, Hook("UpdatePrefix"), null, null);
                    harmony.Patch(pmLate, Hook("LatePrefix"), null, null);
                }
                Active = true;
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                return "patching failed (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }

        public static void Remove()
        {
            Active = false;
            try { harmony?.UnpatchAll(HarmonyId); }
            catch (Exception ex) { ModConsoleSafe(ex.Message); }
            harmony = null;
            clean.Clear();
            pendingUpdate.Fsm = null;
            pendingLate.Fsm = null;
        }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(FsmFastPath).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(FsmFastPath), name);
            return new HarmonyMethod(m);
        }

        /// <summary>Drops references to FSMs of unloaded scenes (always safe: entries are re-verified on demand).</summary>
        public static void ClearCaches()
        {
            clean.Clear();
            pendingUpdate.Fsm = null;
            pendingLate.Fsm = null;
        }

        public static void ResetStats()
        {
            UpdateStats.Reset();
            LateStats.Reset();
            UpdateBlockers.Clear();
            LateBlockers.Clear();
            MismatchLog.Clear();
            MismatchCounts.Clear();
        }

        // ----- Classification -------------------------------------------------------------------------

        static int Flags(Type t)
        {
            if (ReferenceEquals(t, lastType)) return lastFlags;
            if (!typeFlags.TryGetValue(t, out int f))
            {
                f = 0;
                if (Overrides(t, "OnUpdate", Type.EmptyTypes)) f |= FlagUpdate;
                if (Overrides(t, "OnLateUpdate", Type.EmptyTypes)) f |= FlagLate;
                if (Overrides(t, "Init", new[] { typeof(FsmState) })) f |= FlagInit;
                typeFlags[t] = f;
            }
            lastType = t;
            lastFlags = f;
            return f;
        }

        static bool Overrides(Type t, string name, Type[] args)
        {
            try
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
                return m == null || m.DeclaringType != typeof(FsmStateAction);
            }
            catch
            {
                return true; // unknown: treat as overriding (the original will run)
            }
        }

        /// <summary>True if every loop counter of the FSM is known to be 0.</summary>
        static bool IsClean(Fsm fsm)
        {
            FsmState[] states = fsm.States;
            if (clean.TryGetValue(fsm, out FsmState[] known) && ReferenceEquals(known, states))
                return true;
            if (states == null) return false;
            for (int i = 0; i < states.Length; i++)
            {
                FsmState s = states[i];
                if (s == null || s.loopCount != 0) return false;
                // The OnEnter hook invalidates "the FSM of the state". A state owned by another FSM object
                // (never the case in PlayMaker 1.6 at runtime) could change without notice: never cache those.
                FsmAccess.ReadState(s, out Fsm owner, out List<FsmStateAction> _);
                if (!ReferenceEquals(owner, fsm)) return false;
            }
            if (clean.Count >= CleanLimit) clean.Clear();
            clean[fsm] = states;
            return true;
        }

        /// <summary>Checks the active actions. Returns -1 if all of them are inert for the given hook.</summary>
        static int CheckActions(List<FsmStateAction> actions, int count, FsmState state, Fsm fsm, PlayMakerFSM owner, int hookFlag, Dictionary<Type, long> blockers)
        {
            GameObject go = null;
            bool evaluate = false;
            for (int i = 0; i < count; i++)
            {
                FsmStateAction a = actions[i];
                if (a == null) return RunNullAction;
                Type t = a.GetType();
                int f = Flags(t);
                if ((f & (hookFlag | FlagInit)) != 0)
                {
                    // 5.1: a few per-frame actions are evaluated below instead of blocking (Update only).
                    if (hookFlag != FlagUpdate || (f & FlagInit) != 0 || EvaluationOff || !IsEvaluable(t))
                    {
                        if (DetailedStats) Bump(blockers, t);
                        return RunActionOverride;
                    }
                    evaluate = true;
                }
                // FsmStateAction.Init(state): fsmState = state; fsm = state.Fsm; owner = fsm.GameObject.
                // Fsm.GameObject == Owner.gameObject, and Owner is the live component that is running now.
                if (!ReferenceEquals(a.State, state) || !ReferenceEquals(a.Fsm, fsm)) return RunActionInit;
                if (go == null) go = owner.gameObject;
                if (!ReferenceEquals(a.Owner, go)) return RunActionInit;
            }

            if (evaluate)
            {
                // Only once nothing else blocks. Pure tests first (no side effects at all), then the mouse picks.
                // Anything reaching this point without FlagUpdate has no OnUpdate; with it, it is evaluable.
                for (int i = 0; i < count; i++)
                {
                    FsmStateAction a = actions[i];
                    Type t = a.GetType();
                    if ((Flags(t) & FlagUpdate) == 0 || ReferenceEquals(t, MousePickType)) continue;
                    if (!EvaluatePure(a, t)) return RunEvaluatedActive;
                }
                bool picked = false;
                float pickDistance = 0f;
                int pickMask = 0;
                for (int i = 0; i < count; i++)
                {
                    FsmStateAction a = actions[i];
                    if (!ReferenceEquals(a.GetType(), MousePickType)) continue;
                    if (!EvaluateMousePick((HutongGames.PlayMaker.Actions.MousePickEvent)a, go, ref picked, ref pickDistance, ref pickMask))
                        return RunEvaluatedActive;
                }
                evaluatedInert = true;
                hitInfoPending = picked;
            }
            return -1;
        }

        // ----- Evaluated actions (5.1) ----------------------------------------------------------------
        //
        // Per-frame actions that only send events (MousePickEvent, BoolTest, FloatCompare). Their OnUpdate is
        // computed here exactly as PlayMaker 1.6 would (decompiled code, same calls in the same order): when
        // none of them would send an event this frame, the state's Update does nothing else, and the FSM can
        // be skipped like an idle one. The same method calls are made (ActionHelpers.IsMouseOver keeps the shared
        // pick cache identical), and the one side effect - MousePickEvent writes Fsm.RaycastHitInfo - is applied.
        //   MousePickEvent: over = ActionHelpers.IsMouseOver(go, rayDistance, LayerArrayToLayerMask(layerMask,
        //                   invertMask)); over: mouseDown (+ button down), mouseOver, mouseUp (+ button up);
        //                   not over: mouseOff. Fsm.RaycastHitInfo = ActionHelpers.mousePickInfo.
        //   BoolTest:       Fsm.Event(value ? isTrue : isFalse)
        //   FloatCompare:   |f1 - f2| <= tolerance: equal; f1 < f2: lessThan; f1 > f2: greaterThan
        // Fsm.Event(null) does nothing; any non-null event (even an empty one) makes the original run.
        // Where an Fsm would throw (a null field), the original runs and throws as it would.

        //
        // Order. The pure tests are evaluated first; they read FSM variables only. The mouse picks come last, and
        // all picks of one state must use the same ray distance and layer mask: if a pick (or anything after it)
        // turns out to be active, the original runs and repeats the same IsMouseOver calls, and with one
        // (distance, mask) pair a repeated call in the same frame is a pure cache hit (MouseOver re-picks only on
        // a new frame, a longer distance or another mask). So even a "would send" result leaves the shared pick
        // cache exactly as PlayMaker alone would.

        /// <summary>True switches the evaluation off (the actions block as before). For A/B tests and as a kill switch.</summary>
        public static bool EvaluationOff = false;   // set by reflection (abtoggle staticbool)

        // Set by ClassifyUpdate (reset on every call): the skip relies on evaluated actions / must write the hit info.
        static bool evaluatedInert;
        static bool hitInfoPending;

        static readonly Type MousePickType = typeof(HutongGames.PlayMaker.Actions.MousePickEvent);
        static readonly Type BoolTestType = typeof(HutongGames.PlayMaker.Actions.BoolTest);
        static readonly Type FloatCompareType = typeof(HutongGames.PlayMaker.Actions.FloatCompare);

        static bool IsEvaluable(Type t) =>
            ReferenceEquals(t, MousePickType) || ReferenceEquals(t, BoolTestType) || ReferenceEquals(t, FloatCompareType);

        /// <summary>BoolTest / FloatCompare: true if OnUpdate would call Fsm.Event(null), i.e. do nothing.</summary>
        static bool EvaluatePure(FsmStateAction a, Type t)
        {
            if (ReferenceEquals(t, BoolTestType))
            {
                HutongGames.PlayMaker.Actions.BoolTest b = (HutongGames.PlayMaker.Actions.BoolTest)a;
                if (b.boolVariable == null) return false;
                return (b.boolVariable.Value ? b.isTrue : b.isFalse) == null;
            }
            if (ReferenceEquals(t, FloatCompareType))
            {
                HutongGames.PlayMaker.Actions.FloatCompare c = (HutongGames.PlayMaker.Actions.FloatCompare)a;
                if (c.float1 == null || c.float2 == null || c.tolerance == null) return false;
                float f1 = c.float1.Value, f2 = c.float2.Value;
                if (Mathf.Abs(f1 - f2) <= c.tolerance.Value) return c.equal == null;
                if (f1 < f2) return c.lessThan == null;
                if (f1 > f2) return c.greaterThan == null;
                return true;                                        // NaN: no branch sends anything
            }
            return false;
        }

        /// <summary>MousePickEvent.DoMousePickEvent without the Fsm calls. True if it would send no event.</summary>
        static bool EvaluateMousePick(HutongGames.PlayMaker.Actions.MousePickEvent m, GameObject owner, ref bool picked, ref float pickDistance, ref int pickMask)
        {
            // Everything the original dereferences; a null makes it throw, so let it run (and throw) itself.
            if (m.GameObject == null || m.rayDistance == null || m.layerMask == null || m.invertMask == null) return false;
            GameObject target;
            if (m.GameObject.OwnerOption != OwnerDefaultOption.UseOwner)
            {
                if (m.GameObject.GameObject == null) return false;
                target = m.GameObject.GameObject.Value;
            }
            else target = owner;
            FsmInt[] layers = m.layerMask;
            for (int i = 0; i < layers.Length; i++) if (layers[i] == null) return false;

            float distance = m.rayDistance.Value;
            int mask = ActionHelpers.LayerArrayToLayerMask(layers, m.invertMask.Value);
            if (picked)
            {
                // Bitwise float compare: NaN or -0 must not slip through as "equal".
                if (BitConverter.DoubleToInt64Bits(distance) != BitConverter.DoubleToInt64Bits(pickDistance) || mask != pickMask) return false;
            }
            else
            {
                picked = true;
                pickDistance = distance;
                pickMask = mask;
            }

            if (ActionHelpers.IsMouseOver(target, distance, mask))
            {
                if (m.mouseDown != null && Input.GetMouseButtonDown(0)) return false;
                if (m.mouseOver != null) return false;
                if (m.mouseUp != null && Input.GetMouseButtonUp(0)) return false;
                return true;
            }
            return m.mouseOff == null;
        }

        static void Bump(Dictionary<Type, long> d, Type t)
        {
            d.TryGetValue(t, out long n);
            d[t] = n + 1;
        }

        static int ClassifyUpdate(Fsm fsm, PlayMakerFSM owner, out FsmState state)
        {
            state = null;
            evaluatedInert = false;
            hitInfoPending = false;
            if (Fsm.HitBreakpoint) return RunBreakpoint;
            if (!ReferenceEquals(fsm.Owner, owner)) return RunOwner;
            if (FsmExecutionStack.StackCount >= FsmExecutionStack.MaxStackCount) return RunStack;

            int ff = FsmAccess.ReadFsm(fsm, out FsmState active, out FsmState switchTo);
            if ((ff & FsmAccess.EnteredBit) == 0) return RunNotEntered;
            if (switchTo != null) return RunSwitching;
            if (fsm.DelayedEvents.Count != 0) return RunDelayedEvents;
            // A null field with a stored name would be resolved lazily by the ActiveState getter: let PlayMaker do it.
            if (active == null) return RunNoActiveState;

            int sf = FsmAccess.ReadState(active, out Fsm stateFsm, out List<FsmStateAction> actions);
            if (!ReferenceEquals(stateFsm, fsm)) return RunStateFsm;
            state = active;

            int result;
            if ((sf & FsmAccess.FinishedBit) != 0)
            {
                result = SkipIdle;                                  // OnUpdate does nothing at all
            }
            else
            {
                int count = actions != null ? actions.Count : 0;
                if (count == 0)
                {
                    // CheckAllActionsFinished would fire FINISHED for an active state.
                    if ((sf & FsmAccess.ActiveBit) != 0) return RunWillFinish;
                }
                else
                {
                    int r = CheckActions(actions, count, active, fsm, owner, FlagUpdate, UpdateBlockers);
                    if (r >= 0) return r;
                }
                result = SkipTimer;                                 // only StateTime += deltaTime
            }

            if (!IsClean(fsm)) return RunLoopCount;
            return result;
        }

        static int ClassifyLate(Fsm fsm, PlayMakerFSM owner, out FsmState state)
        {
            state = null;
            if (FsmExecutionStack.StackCount >= FsmExecutionStack.MaxStackCount) return RunStack;

            int ff = FsmAccess.ReadFsm(fsm, out FsmState active, out FsmState switchTo);
            if (active == null) return RunNoActiveState;            // lazy ActiveState getter: let PlayMaker run
            if ((ff & FsmAccess.EnteredBit) == 0) return SkipIdle;  // "ActiveState != null && activeStateEntered" is false
            if (!ReferenceEquals(fsm.Owner, owner)) return RunOwner;
            if (switchTo != null) return RunSwitching;

            int sf = FsmAccess.ReadState(active, out Fsm stateFsm, out List<FsmStateAction> actions);
            if (!ReferenceEquals(stateFsm, fsm)) return RunStateFsm;
            state = active;

            int count = actions != null ? actions.Count : 0;
            if (count == 0)
            {
                // CheckAllActionsFinished: fires FINISHED if the state is active and not finished yet.
                if ((sf & FsmAccess.FinishedBit) == 0 && (sf & FsmAccess.ActiveBit) != 0) return RunWillFinish;
            }
            else
            {
                int r = CheckActions(actions, count, active, fsm, owner, FlagLate, LateBlockers);
                if (r >= 0) return r;
            }

            if (!IsClean(fsm)) return RunLoopCount;
            return SkipIdle;
        }

        static void Count(Counters c, int code)
        {
            c.Calls++;
            if (code == SkipIdle) c.SkipIdle++;
            else if (code == SkipTimer) c.SkipTimer++;
            else c.Reasons[code]++;
        }

        // ----- Patches: normal mode -------------------------------------------------------------------

        static void OnEnterPrefix(Fsm ___fsm)
        {
            // Runs before "loopCount++" in FsmState.OnEnter.
            if (___fsm != null) clean.Remove(___fsm);
        }

        static bool UpdatePrefix(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (___fsm == null || ___fsm.Finished) return true;
            int code = ClassifyUpdate(___fsm, __instance, out FsmState state);
            Count(UpdateStats, code);
            if (code >= 0) return true;

            FsmTime.RealtimeBugFix();
            if (code == SkipTimer) FsmAccess.AddStateTime(state, Time.deltaTime);
            if (evaluatedInert) UpdateStats.SkipEvaluated++;
            // MousePickEvent's only side effect besides the pick cache (already updated by the same calls).
            if (hitInfoPending) ___fsm.RaycastHitInfo = ActionHelpers.mousePickInfo;
            return false;
        }

        static bool LatePrefix(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (___fsm == null || ___fsm.Finished) return true;
            int code = ClassifyLate(___fsm, __instance, out FsmState _);
            Count(LateStats, code);
            if (code >= 0) return true;

            FsmVariables.GlobalVariablesSynced = false;
            return false;
        }

        // ----- Patches: shadow mode (always run the original, verify the claim) ------------------------

        struct Pending
        {
            public Fsm Fsm;
            public FsmState State;
            public int Code;
            public int StateFlags;
            public int ActionCount;
            public int DelayedCount;
            public float StateTime;
            public int MaxStack;
            public bool EventSent;          // set by the Fsm.Event hook while this FSM's claimed no-op runs
            public bool HitInfo;            // evaluated MousePickEvent: expected Fsm.RaycastHitInfo / pick cache
            public RaycastHit Hit;
        }

        static Pending pendingUpdate, pendingLate;

        static void Arm(ref Pending p, Fsm fsm, FsmState state, int code, bool advanceTime)
        {
            p.Fsm = fsm;
            p.State = state;
            p.Code = code;
            p.MaxStack = FsmExecutionStack.MaxStackCount;
            p.EventSent = false;
            p.HitInfo = false;
            // LateUpdate never touches delayed events, so they may legitimately be pending: compare, don't require 0.
            p.DelayedCount = fsm.DelayedEvents.Count;
            if (state != null)
            {
                p.StateFlags = FsmAccess.ReadState(state, out Fsm _, out List<FsmStateAction> actions);
                p.ActionCount = actions != null ? actions.Count : 0;
                p.StateTime = advanceTime ? state.StateTime + Time.deltaTime : state.StateTime;
            }
        }

        static void Verify(ref Pending p, Counters c, PlayMakerFSM owner, Fsm fsm, string hook)
        {
            if (p.Fsm == null) return;
            if (!ReferenceEquals(p.Fsm, fsm)) { p.Fsm = null; return; }
            c.ShadowChecked++;

            string problem = null;
            int ff = FsmAccess.ReadFsm(fsm, out FsmState active, out FsmState switchTo);
            if (fsm.Finished) problem = "fsm finished";
            else if (p.State != null && !ReferenceEquals(active, p.State)) problem = "active state changed";
            else if (switchTo != null) problem = "state switch pending";
            else if (fsm.DelayedEvents.Count != p.DelayedCount) problem = "delayed events " + p.DelayedCount + " -> " + fsm.DelayedEvents.Count;
            else if (FsmExecutionStack.MaxStackCount != p.MaxStack) problem = "max stack changed";
            else if (p.State != null)
            {
                int sf = FsmAccess.ReadState(p.State, out Fsm _, out List<FsmStateAction> actions);
                int count = actions != null ? actions.Count : 0;
                if (sf != p.StateFlags) problem = "state flags " + p.StateFlags + " -> " + sf;
                else if (count != p.ActionCount) problem = "active actions " + p.ActionCount + " -> " + count;
                else if (p.State.StateTime != p.StateTime) problem = "state time " + p.StateTime.ToString("R") + " vs " + p.State.StateTime.ToString("R");
            }
            if ((ff & FsmAccess.EnteredBit) == 0 && p.State != null) problem = problem ?? "entered flag cleared";
            // Even an event without a transition (no visible state change) breaks the claim.
            if (p.EventSent) problem = problem ?? "event sent";
            if (p.HitInfo && problem == null)
            {
                if (!SameHit(fsm.RaycastHitInfo, p.Hit)) problem = "raycast hit info differs";
                else if (!SameHit(ActionHelpers.mousePickInfo, p.Hit)) problem = "pick cache changed";
            }

            if (problem != null)
            {
                c.ShadowMismatch++;
                string where;
                try { where = owner.gameObject.name + " [" + fsm.Name + "] state '" + (p.State != null ? p.State.Name : "-") + "'"; }
                catch { where = "(unknown)"; }
                string key = hook + " code " + p.Code + ": " + where + ": " + problem;
                if (MismatchCounts.TryGetValue(key, out long n)) MismatchCounts[key] = n + 1;
                else if (MismatchLog.Count < 50) { MismatchLog.Add(key); MismatchCounts[key] = 1; }
            }
            p.Fsm = null;
        }

        static bool UpdatePrefixShadow(PlayMakerFSM __instance, Fsm ___fsm)
        {
            pendingUpdate.Fsm = null;
            if (___fsm == null || ___fsm.Finished) return true;
            int code = ClassifyUpdate(___fsm, __instance, out FsmState state);
            Count(UpdateStats, code);
            if (code < 0)
            {
                Arm(ref pendingUpdate, ___fsm, state, code, code == SkipTimer);
                if (evaluatedInert) UpdateStats.SkipEvaluated++;
                if (hitInfoPending)
                {
                    pendingUpdate.HitInfo = true;
                    pendingUpdate.Hit = ActionHelpers.mousePickInfo;
                }
            }
            return true;
        }

        static void EventPrefixShadow(Fsm __instance)
        {
            if (pendingUpdate.Fsm != null && ReferenceEquals(pendingUpdate.Fsm, __instance)) pendingUpdate.EventSent = true;
            if (pendingLate.Fsm != null && ReferenceEquals(pendingLate.Fsm, __instance)) pendingLate.EventSent = true;
        }

        static bool SameHit(RaycastHit a, RaycastHit b)
        {
            Vector3 pa = a.point, pb = b.point, na = a.normal, nb = b.normal;
            return ReferenceEquals(a.collider, b.collider) && a.distance.Equals(b.distance) &&
                   pa.x.Equals(pb.x) && pa.y.Equals(pb.y) && pa.z.Equals(pb.z) &&
                   na.x.Equals(nb.x) && na.y.Equals(nb.y) && na.z.Equals(nb.z);
        }

        static void UpdatePostfixShadow(PlayMakerFSM __instance, Fsm ___fsm)
        {
            Verify(ref pendingUpdate, UpdateStats, __instance, ___fsm, "Update");
        }

        static bool LatePrefixShadow(PlayMakerFSM __instance, Fsm ___fsm)
        {
            pendingLate.Fsm = null;
            if (___fsm == null || ___fsm.Finished) return true;
            int code = ClassifyLate(___fsm, __instance, out FsmState state);
            Count(LateStats, code);
            if (code < 0) Arm(ref pendingLate, ___fsm, state, code, false);
            return true;
        }

        static void LatePostfixShadow(PlayMakerFSM __instance, Fsm ___fsm)
        {
            Verify(ref pendingLate, LateStats, __instance, ___fsm, "LateUpdate");
        }

        // ----- Reporting ------------------------------------------------------------------------------

        public static void AppendReport(StringBuilder sb)
        {
            AppendCounters(sb, "Update", UpdateStats, true);
            AppendCounters(sb, "LateUpdate", LateStats, false);
            if (DetailedStats)
            {
                AppendBlockers(sb, "Update blockers (action types with OnUpdate/Init)", UpdateBlockers);
                AppendBlockers(sb, "LateUpdate blockers (action types with OnLateUpdate/Init)", LateBlockers);
            }
            if (MismatchLog.Count > 0)
            {
                sb.AppendLine("Shadow mismatches:");
                foreach (string s in MismatchLog) sb.AppendLine($"  {MismatchCounts[s],8}x  {s}");
            }
        }

        static void AppendCounters(StringBuilder sb, string name, Counters c, bool timer)
        {
            double pct(long v) => c.Calls > 0 ? 100.0 * v / c.Calls : 0.0;
            sb.AppendLine($"{name}: {c.Calls} calls, skipped idle {pct(c.SkipIdle):0.0}%" +
                          (timer ? $", skipped timer-only {pct(c.SkipTimer):0.0}% (evaluated {pct(c.SkipEvaluated):0.0}%)" : "") +
                          (Shadow ? $", shadow checked {c.ShadowChecked}, MISMATCHES {c.ShadowMismatch}" : ""));
            for (int i = 0; i < ReasonCount; i++)
                if (c.Reasons[i] > 0) sb.AppendLine($"    run: {ReasonNames[i],-16} {pct(c.Reasons[i]),5:0.0}%");
        }

        static void AppendBlockers(StringBuilder sb, string title, Dictionary<Type, long> d)
        {
            if (d.Count == 0) return;
            List<KeyValuePair<Type, long>> list = new List<KeyValuePair<Type, long>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.AppendLine(title + ":");
            for (int i = 0; i < list.Count && i < 25; i++)
                sb.AppendLine($"    {list[i].Value,10}  {list[i].Key.Name}");
        }

        static void ModConsoleSafe(string error)
        {
            try { global::MOP.ModConsole.Warn("con.engine.unpatch_failed", error); } catch { }
        }
    }
}
