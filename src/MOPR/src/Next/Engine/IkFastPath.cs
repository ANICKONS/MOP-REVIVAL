// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// SimpleIKSolver fast path (5.1).
//
// The game's CCD IK (SimpleIKSolver: Satsuma suspension arms and shocks, car and floor jacks, tractor and
// flatbed cylinders) runs in LateUpdate and iterates until the end joint is within ~3.5 cm of its target, at
// most 21 times. Most of these chains can never reach their target (a cylinder that only has to point at it),
// so every solver runs all 21 iterations every frame: 19 solvers, 0.24 ms per frame at home (measured).
// On resting inputs most solves change nothing at all (bitwise: 12 of 13 solvers at rest, TestBot ikprobe).
//
// Proof. Solve() is a deterministic function of:
//   * the local position, rotation and scale of every transform from each joint and from the target up to the
//     scene root (the world positions and rotations it reads, and the world-to-local conversion of the
//     rotations it writes, are computed by Unity from exactly these values), and the parents themselves;
//   * the solver's own fields: Target, JointEntities (array, entries, joints, angle restrictions), IsDamping,
//     DampingMax (IsActive false: the original does nothing).
// It writes only the joints' rotations (Transform.rotation, localEulerAngles). When a solve ran on inputs I
// and left every joint's local pose bitwise as it was, then a solve on the same inputs I again computes the
// same values and writes the same values: it can be skipped. So each solver keeps the inputs of its last solve
// that changed nothing; the next LateUpdate compares the live inputs with them (bitwise, -0 and NaN included)
// and skips only on a full match. Anything else (first solve, a joint changed, a parent changed or was
// reparented, a field changed, a solve that moved something) runs the original.
//
// What the skip leaves out: the same-value writes themselves. Unity then sets Transform.hasChanged and
// re-syncs colliders below the joints to the same pose. No game script reads hasChanged (Assembly-CSharp,
// PlayMaker: no reference); MOP's mesh merger compares the values anyway. The colliders keep their pose.
//
// SHADOW mode runs the original anyway and checks that every skippable solve really changed nothing.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Harmony;
using UnityEngine;

namespace MOP.Next.Engine
{
    static class IkFastPath
    {
        const string HarmonyId = "com.anickon.mopr.engine.ik";
        static HarmonyInstance harmony;

        public static bool Installed => harmony != null;
        public static bool Shadow { get; private set; }
        public static string Status { get; private set; } = "not installed";

        /// <summary>A/B measurement only (TestBot): while true, every solve runs.</summary>
        public static bool Bypass { get; set; }

        public static long Calls, Skipped, NoOpRuns, ChangedRuns, ShadowChecked, ShadowMismatch;
        static string lastMismatch;

        [StructLayout(LayoutKind.Explicit)]
        struct Bits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
        }

        static int B(float f) { Bits b = default(Bits); b.F = f; return b.I; }

        /// <summary>The inputs of one solve: chain transforms' local poses and the solver's fields.</summary>
        sealed class Inputs
        {
            public int Count;
            public Transform[] Parents = new Transform[0];
            public int[] Values = new int[0];        // 10 per transform: position, rotation, scale (float bits)
            public Transform Target;
            public SimpleIKSolver.JointEntity[] Joints;
            public object[] Entries = new object[0]; // JointEntity, Joint, AngleRestriction per joint
            public int[] Config = new int[0];        // damping, damping max, 9 restriction values per joint

            public void Ensure(int transforms, int joints)
            {
                if (Parents.Length < transforms) { Parents = new Transform[transforms]; Values = new int[transforms * 10]; }
                if (Entries.Length < joints * 3) { Entries = new object[joints * 3]; Config = new int[2 + joints * 9]; }
            }
        }

        sealed class Entry
        {
            public Transform[] Chain;                // unique transforms: the joints, the target and their ancestors
            public int JointCount;
            public int[] JointSlots;                 // index of each joint in Chain
            public SimpleIKSolver.JointEntity[] ChainJoints;
            public Transform ChainTarget;
            public readonly Inputs Live = new Inputs();
            public readonly Inputs NoOp = new Inputs();
            public bool NoOpValid;
        }

        sealed class RefComparer : IEqualityComparer<SimpleIKSolver>
        {
            public bool Equals(SimpleIKSolver a, SimpleIKSolver b) => ReferenceEquals(a, b);
            public int GetHashCode(SimpleIKSolver o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<SimpleIKSolver, Entry> entries = new Dictionary<SimpleIKSolver, Entry>(64, new RefComparer());
        static Entry pending;            // the solve that runs now (postfix)
        static bool skipping, shadowClaim;

        public static string Install(bool shadow)
        {
            Remove();
            try
            {
                MethodInfo late = typeof(SimpleIKSolver).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (late == null) return Status = "SimpleIKSolver.LateUpdate not found";
                Shadow = shadow;
                harmony = HarmonyInstance.Create(HarmonyId);
                harmony.Patch(late, Hook("Prefix"), Hook("Postfix"), null);
                Status = shadow ? "VERIFY" : "ON";
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                return Status = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Remove()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            ClearCaches();
            Status = "not installed";
        }

        public static void ClearCaches()
        {
            entries.Clear();
            pending = null;
            skipping = shadowClaim = false;
        }

        public static void ResetStats()
        {
            Calls = Skipped = NoOpRuns = ChangedRuns = ShadowChecked = ShadowMismatch = 0;
            lastMismatch = null;
        }

        static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(IkFastPath).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ----- Inputs ---------------------------------------------------------------------------------------

        /// <summary>The joints, the target and all their ancestors, each once. Null if a joint is missing.</summary>
        static bool BuildChain(SimpleIKSolver s, Entry e)
        {
            SimpleIKSolver.JointEntity[] joints = s.JointEntities;
            if (joints == null || joints.Length == 0 || s.Target == null) return false;
            List<Transform> chain = new List<Transform>(16);
            Dictionary<Transform, int> index = new Dictionary<Transform, int>();
            int[] slots = new int[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] == null || joints[i].Joint == null || joints[i].AngleRestrictionRange == null) return false;
                slots[i] = Add(joints[i].Joint, chain, index);
            }
            Add(s.Target, chain, index);
            e.Chain = chain.ToArray();
            e.JointCount = joints.Length;
            e.JointSlots = slots;
            e.ChainJoints = joints;
            e.ChainTarget = s.Target;
            e.NoOpValid = false;
            return true;
        }

        static int Add(Transform t, List<Transform> chain, Dictionary<Transform, int> index)
        {
            int first = -1;
            for (Transform x = t; x != null; x = x.parent)
            {
                int k;
                if (!index.TryGetValue(x, out k))
                {
                    k = chain.Count;
                    index[x] = k;
                    chain.Add(x);
                }
                if (first < 0) first = k;
            }
            return first;
        }

        /// <summary>Reads the live inputs. False if the chain no longer matches the solver (rebuild needed).</summary>
        static bool Capture(SimpleIKSolver s, Entry e, Inputs into)
        {
            SimpleIKSolver.JointEntity[] joints = s.JointEntities;
            if (!ReferenceEquals(joints, e.ChainJoints) || joints.Length != e.JointCount || !ReferenceEquals(s.Target, e.ChainTarget)) return false;
            Transform[] chain = e.Chain;
            into.Ensure(chain.Length, joints.Length);
            into.Count = chain.Length;
            into.Target = s.Target;
            into.Joints = joints;
            int[] v = into.Values;
            for (int i = 0, o = 0; i < chain.Length; i++, o += 10)
            {
                Transform t = chain[i];
                if (t == null) return false;
                into.Parents[i] = t.parent;
                Vector3 p = t.localPosition, sc = t.localScale;
                Quaternion r = t.localRotation;
                v[o] = B(p.x); v[o + 1] = B(p.y); v[o + 2] = B(p.z);
                v[o + 3] = B(r.x); v[o + 4] = B(r.y); v[o + 5] = B(r.z); v[o + 6] = B(r.w);
                v[o + 7] = B(sc.x); v[o + 8] = B(sc.y); v[o + 9] = B(sc.z);
            }
            int[] c = into.Config;
            c[0] = s.IsDamping ? 1 : 0;
            c[1] = B(s.DampingMax);
            for (int j = 0; j < joints.Length; j++)
            {
                SimpleIKSolver.JointEntity je = joints[j];
                if (je == null || je.Joint == null || je.AngleRestrictionRange == null) return false;
                SimpleIKSolver.AngleRestriction a = je.AngleRestrictionRange;
                into.Entries[j * 3] = je;
                into.Entries[j * 3 + 1] = je.Joint;
                into.Entries[j * 3 + 2] = a;
                int k = 2 + j * 9;
                c[k] = (a.xAxis ? 1 : 0) | (a.yAxis ? 2 : 0) | (a.zAxis ? 4 : 0);
                c[k + 1] = B(a.xMin); c[k + 2] = B(a.xMax);
                c[k + 3] = B(a.yMin); c[k + 4] = B(a.yMax);
                c[k + 5] = B(a.zMin); c[k + 6] = B(a.zMax);
                c[k + 7] = 0; c[k + 8] = 0;
            }
            // Every joint must still be the transform the chain was built from.
            for (int j = 0; j < joints.Length; j++)
                if (!ReferenceEquals(joints[j].Joint, chain[e.JointSlots[j]])) return false;
            return true;
        }

        static bool Same(Inputs a, Inputs b, int joints)
        {
            if (a.Count != b.Count || !ReferenceEquals(a.Target, b.Target) || !ReferenceEquals(a.Joints, b.Joints)) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a.Parents[i], b.Parents[i])) return false;
            int n = a.Count * 10;
            int[] va = a.Values, vb = b.Values;
            for (int i = 0; i < n; i++) if (va[i] != vb[i]) return false;
            int m = joints * 3;
            for (int i = 0; i < m; i++) if (!ReferenceEquals(a.Entries[i], b.Entries[i])) return false;
            int cn = 2 + joints * 9;
            for (int i = 0; i < cn; i++) if (a.Config[i] != b.Config[i]) return false;
            return true;
        }

        /// <summary>True if no joint's local pose differs from the captured inputs (the solve changed nothing).</summary>
        static bool JointsUnchanged(Entry e, Inputs before)
        {
            int[] v = before.Values;
            for (int j = 0; j < e.JointCount; j++)
            {
                Transform t = e.Chain[e.JointSlots[j]];
                if (t == null) return false;
                int o = e.JointSlots[j] * 10;
                Vector3 p = t.localPosition, sc = t.localScale;
                Quaternion r = t.localRotation;
                if (v[o] != B(p.x) || v[o + 1] != B(p.y) || v[o + 2] != B(p.z) ||
                    v[o + 3] != B(r.x) || v[o + 4] != B(r.y) || v[o + 5] != B(r.z) || v[o + 6] != B(r.w) ||
                    v[o + 7] != B(sc.x) || v[o + 8] != B(sc.y) || v[o + 9] != B(sc.z)) return false;
            }
            return true;
        }

        // ----- Patches ----------------------------------------------------------------------------------------

        static bool Prefix(SimpleIKSolver __instance)
        {
            pending = null;
            skipping = shadowClaim = false;
            if (Bypass || __instance == null || !__instance.IsActive) return true;   // !IsActive: the original does nothing
            try
            {
                Calls++;
                Entry e;
                if (!entries.TryGetValue(__instance, out e))
                {
                    e = new Entry();
                    if (!BuildChain(__instance, e)) return true;
                    entries[__instance] = e;
                }
                if (!Capture(__instance, e, e.Live))
                {
                    if (!BuildChain(__instance, e) || !Capture(__instance, e, e.Live)) { entries.Remove(__instance); return true; }
                }
                pending = e;
                if (e.NoOpValid && Same(e.Live, e.NoOp, e.JointCount))
                {
                    if (Shadow) { shadowClaim = true; return true; }
                    skipping = true;
                    Skipped++;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return true;
            }
        }

        static void Postfix()
        {
            // Harmony 1.2 runs postfixes even when the prefix skipped the original.
            if (skipping) { skipping = false; pending = null; return; }
            Entry e = pending;
            pending = null;
            if (e == null) return;
            try
            {
                bool noop = JointsUnchanged(e, e.Live);
                if (shadowClaim)
                {
                    shadowClaim = false;
                    ShadowChecked++;
                    if (!noop)
                    {
                        ShadowMismatch++;
                        lastMismatch = e.Chain.Length > 0 && e.Chain[0] != null ? e.Chain[0].name : "?";
                    }
                }
                if (noop)
                {
                    NoOpRuns++;
                    // These inputs are known to give a solve that changes nothing: keep them.
                    Copy(e.Live, e.NoOp, e.JointCount);
                    e.NoOpValid = true;
                }
                else
                {
                    ChangedRuns++;
                    e.NoOpValid = false;
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        static void Copy(Inputs from, Inputs to, int joints)
        {
            to.Ensure(from.Count, joints);
            to.Count = from.Count;
            to.Target = from.Target;
            to.Joints = from.Joints;
            Array.Copy(from.Parents, to.Parents, from.Count);
            Array.Copy(from.Values, to.Values, from.Count * 10);
            Array.Copy(from.Entries, to.Entries, joints * 3);
            Array.Copy(from.Config, to.Config, 2 + joints * 9);
        }

        static void Fail(Exception ex)
        {
            global::MOP.Common.ExceptionManager.New(ex, false, "ENGINE_IK_FAST_PATH");
            Bypass = true;
            ClearCaches();
        }

        public static void AppendReport(StringBuilder sb)
        {
            if (!Installed) { sb.AppendLine("IK fast path: " + Status); return; }
            double pct(long v) => Calls > 0 ? 100.0 * v / Calls : 0.0;
            sb.AppendLine($"IK fast path: {Status}{(Bypass ? " (bypassed)" : "")}, {entries.Count} solvers, {Calls} solves, skipped {pct(Skipped):0.0}%, " +
                          $"ran without a change {pct(NoOpRuns):0.0}%, ran with a change {pct(ChangedRuns):0.0}%" +
                          (Shadow ? $", verify checked {ShadowChecked}, MISMATCHES {ShadowMismatch}" + (lastMismatch != null ? " (last: " + lastMismatch + ")" : "") : ""));
        }
    }
}
