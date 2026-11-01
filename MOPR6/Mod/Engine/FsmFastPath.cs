// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// FSM fast path (exact optimization, requirement 7, design §6; ported from MOPR 5.x Next/Engine/FsmFastPath.cs).
// Every enabled PlayMakerFSM runs Update() and LateUpdate() each frame even when its active state has nothing left to
// do: PlayMaker 1.6 still pushes the FSM on the execution stack, copies the delayed event list, calls Init() on every
// active action and resets the loop counter of every state - twice per frame, for every FSM. This system skips those
// calls only when FsmIdle proves from the live state that the original would change nothing (see FsmIdle).
//
// Modes: On - the proven no-ops are skipped; Verify (7.2) - the original always runs and the claimed "no change" is
// checked afterwards (active state, pending switch, delayed events, stack, state flags, active actions, StateTime,
// no event sent, the pick cache); every mismatch is counted and listed in the report.
// Changes from 5.x: only vanilla FSMs (inventory, 2.1), errors go to Fail (the system stops, the host decides) instead
// of being swallowed, the clean marks live in FsmIdle (shared with sleep) and the OnEnter hook is FsmHooks.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using MOPR6.Catalog;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Engine
{
    public sealed class FsmFastPath : Optimization
    {
        public static readonly FsmFastPath Instance = new FsmFastPath();
        public const string SystemId = "engine.fsm";
        const string HarmonyId = "mopr.engine.fsm";
        /// <summary>FSMs whose inventory verdict is checked again per tick (Vanilla.Sweep).</summary>
        const int SweepPerTick = 50;

        FsmFastPath() : base(SystemId) { }

        public override string Title => "FSM fast path";
        public override Tier Tier => Tier.Exact;

        public sealed class Counters
        {
            public long Calls, SkipIdle, SkipTimer, SkipEvaluated, Checked, Mismatches;
            public readonly long[] Reasons = new long[FsmIdle.ReasonCount];

            public void Reset()
            {
                Calls = SkipIdle = SkipTimer = SkipEvaluated = Checked = Mismatches = 0;
                Array.Clear(Reasons, 0, Reasons.Length);
            }
        }

        public static readonly Counters UpdateStats = new Counters();
        public static readonly Counters LateStats = new Counters();
        static readonly List<string> mismatchLog = new List<string>();
        static readonly Dictionary<string, long> mismatchCounts = new Dictionary<string, long>();

        static HarmonyInstance harmony;
        static bool verify;

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!FsmAccess.Init()) return "field access unavailable (" + FsmAccess.Error + ")";
            if (!Vanilla.Ready) return "no vanilla inventory (" + Vanilla.Error + ")";
            if (!FsmHooks.Installed) return "the FsmState.OnEnter hook is not installed";

            const BindingFlags Decl = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            MethodInfo pmUpdate = typeof(PlayMakerFSM).GetMethod("Update", Decl, null, Type.EmptyTypes, null);
            MethodInfo pmLate = typeof(PlayMakerFSM).GetMethod("LateUpdate", Decl, null, Type.EmptyTypes, null);
            if (pmUpdate == null || pmLate == null) return "PlayMakerFSM.Update / LateUpdate not found";

            verify = mode == SystemMode.Verify;
            UpdateStats.Reset();
            LateStats.Reset();
            mismatchLog.Clear();
            mismatchCounts.Clear();
            pendingUpdate.Fsm = null;
            pendingLate.Fsm = null;
            harmony = HarmonyInstance.Create(HarmonyId);
            if (verify)
            {
                // Every Fsm.Event overload ends in Event(FsmEventTarget, FsmEvent): watch it for claimed no-ops.
                MethodInfo fsmEvent = null;
                foreach (MethodInfo m in typeof(Fsm).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != "Event") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(FsmEventTarget) && ps[1].ParameterType == typeof(FsmEvent)) { fsmEvent = m; break; }
                }
                if (fsmEvent == null) return "Fsm.Event(FsmEventTarget, FsmEvent) not found";
                harmony.Patch(fsmEvent, Hook(nameof(EventPrefixVerify)), null, null);
                harmony.Patch(pmUpdate, Hook(nameof(UpdatePrefixVerify)), Hook(nameof(UpdatePostfixVerify)), null);
                harmony.Patch(pmLate, Hook(nameof(LatePrefixVerify)), Hook(nameof(LatePostfixVerify)), null);
            }
            else
            {
                harmony.Patch(pmUpdate, Hook(nameof(UpdatePrefix)), null, null);
                harmony.Patch(pmLate, Hook(nameof(LatePrefix)), null, null);
            }
            return null;
        }

        protected internal override void OnRemove()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            pendingUpdate.Fsm = null;
            pendingLate.Fsm = null;
        }

        protected internal override void OnTick(double now) { Vanilla.Sweep(SweepPerTick); }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(FsmFastPath).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(FsmFastPath), name);
            return new HarmonyMethod(m);
        }

        static void Count(Counters c, int code)
        {
            c.Calls++;
            if (code == FsmIdle.SkipIdle) c.SkipIdle++;
            else if (code == FsmIdle.SkipTimer) c.SkipTimer++;
            else c.Reasons[code]++;
        }

        static string Where(PlayMakerFSM pm)
        {
            if (pm == null) return null;
            StringBuilder sb = new StringBuilder(pm.gameObject.name);
            for (Transform p = pm.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.Append('#').Append(pm.FsmName).ToString();
        }

        // ----- On ---------------------------------------------------------------------------------------

        static bool UpdatePrefix(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (!Instance.Live || ___fsm == null) return true;
            try
            {
                if (___fsm.Finished) return true;
                FsmState state;
                int code = FsmIdle.ClassifyUpdate(___fsm, __instance, out state);
                Count(UpdateStats, code);
                if (code >= 0) return true;

                FsmTime.RealtimeBugFix();
                if (code == FsmIdle.SkipTimer) FsmAccess.AddStateTime(state, Time.deltaTime);
                if (FsmIdle.EvaluatedInert) UpdateStats.SkipEvaluated++;
                // MousePickEvent's only side effect besides the pick cache (already updated by the same calls).
                if (FsmIdle.HitInfoPending) ___fsm.RaycastHitInfo = ActionHelpers.mousePickInfo;
                return false;
            }
            catch (Exception ex)
            {
                Instance.Fail("Update", ex, Where(__instance));
                return true;
            }
        }

        static bool LatePrefix(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (!Instance.Live || ___fsm == null) return true;
            try
            {
                if (___fsm.Finished) return true;
                FsmState state;
                int code = FsmIdle.ClassifyLate(___fsm, __instance, out state);
                Count(LateStats, code);
                if (code >= 0) return true;
                FsmVariables.GlobalVariablesSynced = false;
                return false;
            }
            catch (Exception ex)
            {
                Instance.Fail("LateUpdate", ex, Where(__instance));
                return true;
            }
        }

        // ----- Verify: the original always runs, the claim is checked --------------------------------------

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
            public bool EventSent;      // set by the Fsm.Event hook while this FSM's claimed no-op runs
            public bool HitInfo;        // evaluated MousePickEvent: expected Fsm.RaycastHitInfo / pick cache
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
                Fsm unused;
                List<FsmStateAction> actions;
                p.StateFlags = FsmAccess.ReadState(state, out unused, out actions);
                p.ActionCount = actions != null ? actions.Count : 0;
                p.StateTime = advanceTime ? state.StateTime + Time.deltaTime : state.StateTime;
            }
        }

        static void Check(ref Pending p, Counters c, PlayMakerFSM owner, Fsm fsm, string hook)
        {
            if (p.Fsm == null) return;
            if (!ReferenceEquals(p.Fsm, fsm)) { p.Fsm = null; return; }
            c.Checked++;

            string problem = null;
            FsmState active, switchTo;
            int ff = FsmAccess.ReadFsm(fsm, out active, out switchTo);
            if (fsm.Finished) problem = "fsm finished";
            else if (p.State != null && !ReferenceEquals(active, p.State)) problem = "active state changed";
            else if (switchTo != null) problem = "state switch pending";
            else if (fsm.DelayedEvents.Count != p.DelayedCount) problem = "delayed events " + p.DelayedCount + " -> " + fsm.DelayedEvents.Count;
            else if (FsmExecutionStack.MaxStackCount != p.MaxStack) problem = "max stack changed";
            else if (p.State != null)
            {
                Fsm unused;
                List<FsmStateAction> actions;
                int sf = FsmAccess.ReadState(p.State, out unused, out actions);
                int count = actions != null ? actions.Count : 0;
                if (sf != p.StateFlags) problem = "state flags " + p.StateFlags + " -> " + sf;
                else if (count != p.ActionCount) problem = "active actions " + p.ActionCount + " -> " + count;
                else if (p.State.StateTime != p.StateTime) problem = "state time " + p.StateTime.ToString("R") + " vs " + p.State.StateTime.ToString("R");
            }
            if ((ff & FsmAccess.EnteredBit) == 0 && p.State != null && problem == null) problem = "entered flag cleared";
            // Even an event without a transition (no visible state change) breaks the claim.
            if (p.EventSent && problem == null) problem = "event sent";
            if (p.HitInfo && problem == null)
            {
                if (!SameHit(fsm.RaycastHitInfo, p.Hit)) problem = "raycast hit info differs";
                else if (!SameHit(ActionHelpers.mousePickInfo, p.Hit)) problem = "pick cache changed";
            }
            p.Fsm = null;
            if (problem == null) return;

            c.Mismatches++;
            string key = hook + " code " + p.Code + ": " + Where(owner) + " state '" + (p.State != null ? p.State.Name : "-") + "': " + problem;
            long n;
            if (mismatchCounts.TryGetValue(key, out n)) mismatchCounts[key] = n + 1;
            else if (mismatchLog.Count < 50) { mismatchLog.Add(key); mismatchCounts[key] = 1; }
        }

        static bool SameHit(RaycastHit a, RaycastHit b)
        {
            Vector3 pa = a.point, pb = b.point, na = a.normal, nb = b.normal;
            return ReferenceEquals(a.collider, b.collider) && a.distance.Equals(b.distance) &&
                   pa.x.Equals(pb.x) && pa.y.Equals(pb.y) && pa.z.Equals(pb.z) &&
                   na.x.Equals(nb.x) && na.y.Equals(nb.y) && na.z.Equals(nb.z);
        }

        static bool UpdatePrefixVerify(PlayMakerFSM __instance, Fsm ___fsm)
        {
            pendingUpdate.Fsm = null;
            if (!Instance.Live || ___fsm == null) return true;
            try
            {
                if (___fsm.Finished) return true;
                FsmState state;
                int code = FsmIdle.ClassifyUpdate(___fsm, __instance, out state);
                Count(UpdateStats, code);
                if (code < 0)
                {
                    Arm(ref pendingUpdate, ___fsm, state, code, code == FsmIdle.SkipTimer);
                    if (FsmIdle.EvaluatedInert) UpdateStats.SkipEvaluated++;
                    if (FsmIdle.HitInfoPending)
                    {
                        pendingUpdate.HitInfo = true;
                        pendingUpdate.Hit = ActionHelpers.mousePickInfo;
                    }
                }
            }
            catch (Exception ex)
            {
                pendingUpdate.Fsm = null;
                Instance.Fail("Update/verify", ex, Where(__instance));
            }
            return true;
        }

        static void EventPrefixVerify(Fsm __instance)
        {
            if (pendingUpdate.Fsm != null && ReferenceEquals(pendingUpdate.Fsm, __instance)) pendingUpdate.EventSent = true;
            if (pendingLate.Fsm != null && ReferenceEquals(pendingLate.Fsm, __instance)) pendingLate.EventSent = true;
        }

        static void UpdatePostfixVerify(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (pendingUpdate.Fsm == null) return;
            try { Check(ref pendingUpdate, UpdateStats, __instance, ___fsm, "Update"); }
            catch (Exception ex)
            {
                pendingUpdate.Fsm = null;
                Instance.Fail("Update/check", ex, Where(__instance));
            }
        }

        static bool LatePrefixVerify(PlayMakerFSM __instance, Fsm ___fsm)
        {
            pendingLate.Fsm = null;
            if (!Instance.Live || ___fsm == null) return true;
            try
            {
                if (___fsm.Finished) return true;
                FsmState state;
                int code = FsmIdle.ClassifyLate(___fsm, __instance, out state);
                Count(LateStats, code);
                if (code < 0) Arm(ref pendingLate, ___fsm, state, code, false);
            }
            catch (Exception ex)
            {
                pendingLate.Fsm = null;
                Instance.Fail("LateUpdate/verify", ex, Where(__instance));
            }
            return true;
        }

        static void LatePostfixVerify(PlayMakerFSM __instance, Fsm ___fsm)
        {
            if (pendingLate.Fsm == null) return;
            try { Check(ref pendingLate, LateStats, __instance, ___fsm, "LateUpdate"); }
            catch (Exception ex)
            {
                pendingLate.Fsm = null;
                Instance.Fail("LateUpdate/check", ex, Where(__instance));
            }
        }

        // ----- Report -------------------------------------------------------------------------------------

        public override long Mismatches => UpdateStats.Mismatches + LateStats.Mismatches;
        public override bool DefaultOn => true;

        protected internal override void OnScene()
        {
            FsmIdle.Clear();
            pendingUpdate.Fsm = null;
            pendingLate.Fsm = null;
        }

        public override void Report(StringBuilder sb)
        {
            AppendCounters(sb, "Update", UpdateStats, true);
            AppendCounters(sb, "LateUpdate", LateStats, false);
            sb.Append("  ").Append(Vanilla.Status()).Append('\n');
            foreach (string s in mismatchLog) sb.Append("  MISMATCH ").Append(mismatchCounts[s]).Append("x ").Append(s).Append('\n');
        }

        static void AppendCounters(StringBuilder sb, string name, Counters c, bool timer)
        {
            double calls = c.Calls > 0 ? c.Calls : 1;
            sb.Append("  ").Append(name).Append(": ").Append(c.Calls).Append(" calls, skipped idle ").Append((100.0 * c.SkipIdle / calls).ToString("0.0")).Append('%');
            if (timer)
                sb.Append(", timer-only ").Append((100.0 * c.SkipTimer / calls).ToString("0.0")).Append("% (evaluated ")
                  .Append((100.0 * c.SkipEvaluated / calls).ToString("0.0")).Append("%)");
            if (verify) sb.Append(", checked ").Append(c.Checked).Append(", mismatches ").Append(c.Mismatches);
            sb.Append('\n');
            for (int i = 0; i < FsmIdle.ReasonCount; i++)
                if (c.Reasons[i] > 0)
                    sb.Append("    run: ").Append(FsmIdle.ReasonNames[i]).Append(' ').Append((100.0 * c.Reasons[i] / calls).ToString("0.0")).Append("%\n");
        }
    }
}
