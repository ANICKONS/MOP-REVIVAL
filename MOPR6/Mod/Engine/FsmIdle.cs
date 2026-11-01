// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The idle classifier of PlayMaker FSMs: would PlayMakerFSM.Update / LateUpdate change anything this frame? Shared by
// the FSM fast path (task 25) and, later, script sleep (design §7), which both act only on a proven "no change".
// Ported from MOPR 5.x (Next/Engine/FsmFastPath.cs) with the same proof, line by line against PlayMaker 1.6:
//
//   PlayMakerFSM.Update -> Fsm.Update:
//     FsmTime.RealtimeBugFix()                 -> the caller does it too (sets a static flag)
//     owner == null -> return                  -> required: fsm.Owner is this live component
//     FsmExecutionStack.Push/Pop               -> balanced; MaxStackCount would not grow (checked)
//     if (!activeStateEntered) Continue()      -> required: entered
//     UpdateDelayedEvents()                    -> required: no delayed events
//     UpdateState(ActiveState):
//       state.Fsm = this                       -> required: state.fsm is already this FSM
//       state.OnUpdate():
//         if finished -> nothing               -> SkipIdle
//         else StateTime += deltaTime          -> SkipTimer: the caller does exactly this (same IL)
//              foreach active action: Init(state); OnUpdate()
//                                              -> required: the action type overrides neither Init nor OnUpdate, and
//                                                 Init would write the values it already has - or it is an evaluated
//                                                 action that would send nothing (below)
//              CheckAllActionsFinished()       -> no-op while actions are active (or the state is inactive)
//       UpdateStateChanges():
//         while (switchToState != null) ...    -> required: no pending state switch
//         foreach state: ResetLoopCount()      -> required: every loopCount is already 0 ("clean")
//   PlayMakerFSM.LateUpdate: FsmVariables.GlobalVariablesSynced = false (the caller does it too); Fsm.LateUpdate with
//   the same reasoning for OnLateUpdate (no StateTime; CheckAllActionsFinished must be a no-op).
//
// "Clean": loopCount is only incremented by FsmState.OnEnter and only cleared by the reset loop above. The OnEnter hook
// (FsmHooks) drops the FSM's clean mark before the counter changes; an FSM is marked again only after all its loop
// counters were read as 0, together with the States array reference (a replaced array invalidates the mark).
//
// Evaluated actions: MousePickEvent, BoolTest and FloatCompare only send events. Their OnUpdate is computed here as
// PlayMaker would (the same calls in the same order); when none would send an event this frame the state does nothing
// else. The same IsMouseOver calls keep the shared pick cache identical; MousePickEvent's write of Fsm.RaycastHitInfo
// is applied by the caller (HitInfoPending). Pure tests first (they only read variables), mouse picks last, all picks
// of a state with one distance and mask - so even a "would send" leaves the pick cache as PlayMaker alone would.
//
// Only vanilla FSMs (Catalog.Vanilla, requirement 2.1): one dictionary lookup per call holds both the clean mark and
// the inventory verdict.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MOPR6.Catalog;
using UnityEngine;

namespace MOPR6.Engine
{
    public static class FsmIdle
    {
        // >= 0: the original must run (reason). < 0: provably no change.
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
        public const int RunEvaluatedActive = 13;
        public const int RunNotVanilla = 14;
        public const int ReasonCount = 15;

        public const int SkipIdle = -1;      // state finished / nothing to update
        public const int SkipTimer = -2;     // only StateTime advances

        public static readonly string[] ReasonNames =
        {
            "breakpoint", "not-entered", "delayed-events", "switching", "owner", "stack", "no-active-state",
            "state-fsm", "will-finish", "null-action", "action-override", "action-init", "loop-count",
            "evaluated-active", "not-vanilla"
        };

        /// <summary>Per FSM: the clean mark (States array whose loop counters were all 0) and the inventory verdict.</summary>
        public sealed class Info
        {
            public FsmState[] CleanStates;
            public Vanilla.Verdict Vanilla;
        }

        sealed class RefComparer : IEqualityComparer<Fsm>
        {
            public bool Equals(Fsm a, Fsm b) => ReferenceEquals(a, b);
            public int GetHashCode(Fsm o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<Fsm, Info> infos = new Dictionary<Fsm, Info>(8192, new RefComparer());
        const int InfoLimit = 30000;

        const int FlagUpdate = 1, FlagLate = 2, FlagInit = 4;
        static readonly Dictionary<Type, int> typeFlags = new Dictionary<Type, int>();
        static Type lastType;
        static int lastFlags;

        /// <summary>Kill switch of the evaluated actions (A/B tests): they block like other per-frame actions.</summary>
        public static bool EvaluationOff;

        /// <summary>Set by ClassifyUpdate: the skip relies on evaluated actions / Fsm.RaycastHitInfo must be written.</summary>
        public static bool EvaluatedInert, HitInfoPending;

        /// <summary>Optional: action types that blocked (detailed statistics). Null = not collected.</summary>
        public static Dictionary<Type, long> UpdateBlockers, LateBlockers;

        public static int Count => infos.Count;

        /// <summary>A new scene, or a system was removed: forget every FSM (marks are re-verified on demand).</summary>
        public static void Clear() { infos.Clear(); }

        /// <summary>From the FsmState.OnEnter hook, before loopCount++: the FSM is not clean any more.</summary>
        public static void OnEnter(Fsm fsm)
        {
            Info i;
            if (fsm != null && infos.TryGetValue(fsm, out i)) i.CleanStates = null;
        }

        static Info InfoFor(Fsm fsm, PlayMakerFSM owner)
        {
            Info i;
            if (infos.TryGetValue(fsm, out i)) return i;
            if (infos.Count >= InfoLimit) infos.Clear();
            i = new Info { Vanilla = Vanilla.For(owner) };
            infos[fsm] = i;
            return i;
        }

        static int Flags(Type t)
        {
            if (ReferenceEquals(t, lastType)) return lastFlags;
            int f;
            if (!typeFlags.TryGetValue(t, out f))
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

        /// <summary>Does the action type have its own version of the method (true also when unsure)?</summary>
        public static bool Overrides(Type t, string name, Type[] args)
        {
            try
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
                return m == null || m.DeclaringType != typeof(FsmStateAction);
            }
            catch (AmbiguousMatchException) { return true; } // catch-ok: a "new" method hides the base one - treated as an override, the original runs
        }

        static bool IsClean(Fsm fsm, Info info)
        {
            FsmState[] states = fsm.States;
            if (states == null) return false;
            if (ReferenceEquals(info.CleanStates, states)) return true;
            for (int i = 0; i < states.Length; i++)
            {
                FsmState s = states[i];
                if (s == null || s.loopCount != 0) return false;
                // The OnEnter hook reports "the FSM of the state". A state owned by another FSM object (never the case
                // in PlayMaker 1.6 at run time) could change without notice: never mark those.
                Fsm owner;
                List<FsmStateAction> unused;
                FsmAccess.ReadState(s, out owner, out unused);
                if (!ReferenceEquals(owner, fsm)) return false;
            }
            info.CleanStates = states;
            return true;
        }

        static void Bump(Dictionary<Type, long> d, Type t)
        {
            if (d == null) return;
            long n;
            d.TryGetValue(t, out n);
            d[t] = n + 1;
        }

        /// <summary>Returns -1 if all active actions are inert for the hook, else the reason.</summary>
        static int CheckActions(List<FsmStateAction> actions, int count, FsmState state, Fsm fsm, PlayMakerFSM owner, int hookFlag)
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
                    if (hookFlag != FlagUpdate || (f & FlagInit) != 0 || EvaluationOff || !IsEvaluable(t))
                    {
                        Bump(hookFlag == FlagUpdate ? UpdateBlockers : LateBlockers, t);
                        return RunActionOverride;
                    }
                    evaluate = true;
                }
                // FsmStateAction.Init(state): fsmState = state; fsm = state.Fsm; owner = fsm.GameObject.
                if (!ReferenceEquals(a.State, state) || !ReferenceEquals(a.Fsm, fsm)) return RunActionInit;
                if (go == null) go = owner.gameObject;
                if (!ReferenceEquals(a.Owner, go)) return RunActionInit;
            }

            if (evaluate)
            {
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
                    if (!EvaluateMousePick((MousePickEvent)a, go, ref picked, ref pickDistance, ref pickMask)) return RunEvaluatedActive;
                }
                EvaluatedInert = true;
                HitInfoPending = picked;
            }
            return -1;
        }

        static readonly Type MousePickType = typeof(MousePickEvent);
        static readonly Type BoolTestType = typeof(BoolTest);
        static readonly Type FloatCompareType = typeof(FloatCompare);

        static bool IsEvaluable(Type t) => ReferenceEquals(t, MousePickType) || ReferenceEquals(t, BoolTestType) || ReferenceEquals(t, FloatCompareType);

        /// <summary>BoolTest / FloatCompare: true if OnUpdate would call Fsm.Event(null), i.e. do nothing.</summary>
        static bool EvaluatePure(FsmStateAction a, Type t)
        {
            if (ReferenceEquals(t, BoolTestType))
            {
                BoolTest b = (BoolTest)a;
                if (b.boolVariable == null) return false;
                return (b.boolVariable.Value ? b.isTrue : b.isFalse) == null;
            }
            if (ReferenceEquals(t, FloatCompareType))
            {
                FloatCompare c = (FloatCompare)a;
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
        static bool EvaluateMousePick(MousePickEvent m, GameObject owner, ref bool picked, ref float pickDistance, ref int pickMask)
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

        /// <summary>Would PlayMakerFSM.Update change anything? See the header. <paramref name="state"/>: the active state.</summary>
        public static int ClassifyUpdate(Fsm fsm, PlayMakerFSM owner, out FsmState state)
        {
            state = null;
            EvaluatedInert = false;
            HitInfoPending = false;
            if (Fsm.HitBreakpoint) return RunBreakpoint;
            if (!ReferenceEquals(fsm.Owner, owner)) return RunOwner;
            Info info = InfoFor(fsm, owner);
            if (!info.Vanilla.Value) return RunNotVanilla;
            if (FsmExecutionStack.StackCount >= FsmExecutionStack.MaxStackCount) return RunStack;

            FsmState active, switchTo;
            int ff = FsmAccess.ReadFsm(fsm, out active, out switchTo);
            if ((ff & FsmAccess.EnteredBit) == 0) return RunNotEntered;
            if (switchTo != null) return RunSwitching;
            if (fsm.DelayedEvents.Count != 0) return RunDelayedEvents;
            // A null field with a stored name would be resolved lazily by the ActiveState getter: let PlayMaker do it.
            if (active == null) return RunNoActiveState;

            Fsm stateFsm;
            List<FsmStateAction> actions;
            int sf = FsmAccess.ReadState(active, out stateFsm, out actions);
            if (!ReferenceEquals(stateFsm, fsm)) return RunStateFsm;
            state = active;

            int result;
            if ((sf & FsmAccess.FinishedBit) != 0) result = SkipIdle;   // OnUpdate does nothing at all
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
                    int r = CheckActions(actions, count, active, fsm, owner, FlagUpdate);
                    if (r >= 0) return r;
                }
                result = SkipTimer;                                    // only StateTime += deltaTime
            }

            if (!IsClean(fsm, info)) return RunLoopCount;
            return result;
        }

        /// <summary>Would PlayMakerFSM.LateUpdate change anything (besides GlobalVariablesSynced)?</summary>
        public static int ClassifyLate(Fsm fsm, PlayMakerFSM owner, out FsmState state)
        {
            state = null;
            // Owner and inventory first (5.x skipped a not-entered FSM before these checks): only vanilla FSMs, 2.1.
            if (!ReferenceEquals(fsm.Owner, owner)) return RunOwner;
            Info info = InfoFor(fsm, owner);
            if (!info.Vanilla.Value) return RunNotVanilla;
            if (FsmExecutionStack.StackCount >= FsmExecutionStack.MaxStackCount) return RunStack;

            FsmState active, switchTo;
            int ff = FsmAccess.ReadFsm(fsm, out active, out switchTo);
            if (active == null) return RunNoActiveState;               // lazy ActiveState getter: let PlayMaker run
            if ((ff & FsmAccess.EnteredBit) == 0) return SkipIdle;     // "ActiveState != null && activeStateEntered" is false
            if (switchTo != null) return RunSwitching;

            Fsm stateFsm;
            List<FsmStateAction> actions;
            int sf = FsmAccess.ReadState(active, out stateFsm, out actions);
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
                int r = CheckActions(actions, count, active, fsm, owner, FlagLate);
                if (r >= 0) return r;
            }

            if (!IsClean(fsm, info)) return RunLoopCount;
            return SkipIdle;
        }
    }
}
