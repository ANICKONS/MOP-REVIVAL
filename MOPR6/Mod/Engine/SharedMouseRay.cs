// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Shared mouse ray (design §6; ported from MOPR 5.x Next/Engine/SharedMouseRay.cs).
// Every part of the Satsuma has a "Removal" FSM with a MousePickEvent action that asks every frame "is the mouse over
// me?". PlayMaker answers through ActionHelpers.MouseOver / IsMouseOver / MousePick, which keep a one-frame cache for
// ONE layer mask only; the game interleaves about a dozen masks, so ActionHelpers.DoMousePick casts a new ray from
// Camera.main almost every time. Measured in 5.x: 300 queries and 107 raycasts per frame next to the car, 181 per
// frame while seated in it in the village (2 ms per frame).
// Only DoMousePick is replaced; PlayMaker's own cache logic in MouseOver / MousePick runs unchanged, so callers see the
// original semantics (MousePick returning the cached hit of a longer ray, Fsm.RaycastHitInfo). The replacement stores
// what Physics.Raycast(ray, out mousePickInfo, distance, mask) would store, from one ray per layer mask per frame
// (cast once, as far as that mask is ever asked for), and sets PlayMaker's three private cache fields as the original
// does. A cast is redone when Camera.main is another camera, when the camera moved, turned or zoomed (mouse look runs
// between FSMs), and when the hit collider was disabled, deactivated, moved or turned since. On a miss the struct is
// left as Unity 5.0 leaves it (only the collider cleared).
// The one case a proof does not cover: ANOTHER collider that moves into the ray during the same frame (between two FSM
// updates); the shared answer then comes one frame late for some FSMs. Verify runs the original DoMousePick as well
// and compares every field of the stored hit: 5.x measured about 985 000 picks (home, next to and in the car,
// driving, camera turning), 0 different. Acceptance in 6.0: 0 on the harness scenarios (requirement 7.4).
// Changes from 5.x: errors go to Fail (the system stops, the host decides) instead of a private bypass.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Engine
{
    public sealed class SharedMouseRay : Optimization
    {
        public static readonly SharedMouseRay Instance = new SharedMouseRay();
        public const string SystemId = "engine.mouseray";
        const string HarmonyId = "mopr.engine.mouseray";
        const int MaxMasks = 32;

        SharedMouseRay() : base(SystemId) { }

        public override string Title => "Shared mouse ray";
        public override Tier Tier => Tier.Exact;
        public override bool DefaultOn => true;

        struct Entry
        {
            public int Mask;
            public float Distance;          // length of the cast ray
            public bool HasHit;
            public RaycastHit Hit;
            public Collider Collider;
            public GameObject GameObject;
            public Transform HitTransform;  // the hit collider's transform and its world pose at cast time
            public Vector3 HitPosition;
            public Quaternion HitRotation;
        }

        static HarmonyInstance harmony;
        static bool verify;

        static readonly Entry[] entries = new Entry[MaxMasks];
        static int entryCount;
        static readonly int[] hintMasks = new int[MaxMasks];
        static readonly float[] hintDistances = new float[MaxMasks];
        static int hintCount;

        static int frame = -1;
        static Camera camera;
        static Transform cameraTransform;
        static Vector3 cameraPosition;
        static Quaternion cameraRotation;
        static float cameraFov;
        static Ray ray;

        public static long Queries, Raycasts, CameraFlushes, StaleHits, Checked, Mismatched;
        static string lastMismatch;

        protected internal override string OnInstall(SystemMode mode)
        {
            Type helpers = typeof(ActionHelpers);
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo pickInfo = helpers.GetField("mousePickInfo", S);
            FieldInfo time = helpers.GetField("mousePickRaycastTime", S);
            FieldInfo distance = helpers.GetField("mousePickDistanceUsed", S);
            FieldInfo mask = helpers.GetField("mousePickLayerMaskUsed", S);
            MethodInfo doPick = helpers.GetMethod("DoMousePick", S, null, new[] { typeof(float), typeof(int) }, null);
            if (pickInfo == null || pickInfo.FieldType != typeof(RaycastHit) || doPick == null ||
                time == null || time.FieldType != typeof(float) || distance == null || distance.FieldType != typeof(float) ||
                mask == null || mask.FieldType != typeof(int))
                return "PlayMaker ActionHelpers changed";
            clearCollider = BuildClearCollider();

            verify = mode == SystemMode.Verify;
            Queries = Raycasts = CameraFlushes = StaleHits = Checked = Mismatched = 0;
            lastMismatch = null;
            Clear();
            harmony = HarmonyInstance.Create(HarmonyId);
            if (verify) harmony.Patch(doPick, Hook(nameof(PickPrefixVerify)), Hook(nameof(PickPostfixVerify)), null);
            else harmony.Patch(doPick, Hook(nameof(PickPrefix)), null, null);
            return null;
        }

        protected internal override void OnRemove()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            Clear();
        }

        protected internal override void OnScene() { Clear(); }

        static void Clear()
        {
            frame = -1;
            entryCount = 0;
            camera = null;
            cameraTransform = null;
            armed = false;
            for (int i = 0; i < entries.Length; i++) entries[i] = default(Entry);
        }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(SharedMouseRay).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(SharedMouseRay), name);
            return new HarmonyMethod(m);
        }

        // ----- The cache ---------------------------------------------------------------------------------------

        const int PickNoCamera = 0, PickMiss = 1, PickHit = 2;

        /// <summary>
        /// Physics.Raycast(Camera.main.ScreenPointToRay(Input.mousePosition), out hit, distance, mask) from the shared
        /// rays: PickHit (hit = the hit), PickMiss, or PickNoCamera (the original then does nothing at all).
        /// </summary>
        static int Pick(float distance, int layerMask, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            Camera main = Camera.main;                 // DoMousePick looks it up itself: another camera, another ray
            if (main == null) return PickNoCamera;

            int f = Time.frameCount;
            if (f != frame || !ReferenceEquals(main, camera))
            {
                frame = f;
                entryCount = 0;
                camera = main;
                cameraTransform = main.transform;
                Aim();
            }
            else
            {
                Vector3 p = cameraTransform.position;
                Quaternion q = cameraTransform.rotation;
                float fov = camera.fieldOfView;
                if (p.x != cameraPosition.x || p.y != cameraPosition.y || p.z != cameraPosition.z ||
                    q.x != cameraRotation.x || q.y != cameraRotation.y || q.z != cameraRotation.z || q.w != cameraRotation.w ||
                    fov != cameraFov)
                {
                    CameraFlushes++;
                    entryCount = 0;
                    Aim();
                }
            }

            Queries++;
            int index = -1;
            for (int i = 0; i < entryCount; i++)
            {
                if (entries[i].Mask != layerMask) continue;
                if (entries[i].Distance < distance || IsStale(ref entries[i])) Cast(i, distance, layerMask);
                index = i;
                break;
            }
            if (index < 0)
            {
                if (entryCount >= MaxMasks) entryCount = 0;   // never happens in practice (about 12 masks)
                index = entryCount++;
                Cast(index, distance, layerMask);
            }

            // The closest hit along the ray, if a ray of exactly this length reaches it.
            if (entries[index].HasHit && entries[index].Hit.distance <= distance)
            {
                hit = entries[index].Hit;
                return PickHit;
            }
            return PickMiss;
        }

        // Unity 5.0 Physics.Raycast on a miss clears only RaycastHit.m_Collider and leaves the other fields of whatever
        // the struct held (measured with the verify mode in 5.x). The emulation does the same.
        delegate void ClearColliderFn(ref RaycastHit hit);
        static ClearColliderFn clearCollider;

        static ClearColliderFn BuildClearCollider()
        {
            FieldInfo f = typeof(RaycastHit).GetField("m_Collider", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null || f.FieldType != typeof(Collider)) throw new MissingFieldException("RaycastHit", "m_Collider");
            DynamicMethod dm = new DynamicMethod("MOPR_ClearHitCollider", typeof(void), new[] { typeof(RaycastHit).MakeByRefType() }, typeof(SharedMouseRay), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stfld, f);
            il.Emit(OpCodes.Ret);
            ClearColliderFn fn = (ClearColliderFn)dm.CreateDelegate(typeof(ClearColliderFn));

            RaycastHit probe = default(RaycastHit);   // self-test: the other fields must survive
            probe.point = new Vector3(1f, 2f, 3f);
            probe.distance = 4f;
            fn(ref probe);
            if (probe.collider != null || probe.point.y != 2f || probe.distance != 4f)
                throw new InvalidOperationException("RaycastHit collider reset self-test failed");
            return fn;
        }

        static void Aim()
        {
            cameraPosition = cameraTransform.position;
            cameraRotation = cameraTransform.rotation;
            cameraFov = camera.fieldOfView;
            ray = camera.ScreenPointToRay(Input.mousePosition);
        }

        /// <summary>The hit collider is gone, switched off or moved since the cast: a fresh ray might not hit it.</summary>
        static bool IsStale(ref Entry e)
        {
            if (!e.HasHit) return false;
            if (e.Collider == null || !e.Collider.enabled || !e.GameObject.activeInHierarchy)
            {
                StaleHits++;
                return true;
            }
            Vector3 p = e.HitTransform.position;
            Quaternion q = e.HitTransform.rotation;
            if (p.x != e.HitPosition.x || p.y != e.HitPosition.y || p.z != e.HitPosition.z ||
                q.x != e.HitRotation.x || q.y != e.HitRotation.y || q.z != e.HitRotation.z || q.w != e.HitRotation.w)
            {
                StaleHits++;
                return true;
            }
            return false;
        }

        static void Cast(int index, float distance, int layerMask)
        {
            // As far as this mask was ever asked for, so the longer queries of this frame hit the cache too.
            float length = Mathf.Max(distance, Hint(layerMask, distance));
            RaycastHit hit;
            bool any = Physics.Raycast(ray, out hit, length, layerMask);
            Raycasts++;
            entries[index].Mask = layerMask;
            entries[index].Distance = length;
            entries[index].HasHit = any && hit.collider != null;
            entries[index].Hit = entries[index].HasHit ? hit : default(RaycastHit);
            entries[index].Collider = entries[index].HasHit ? hit.collider : null;
            entries[index].GameObject = entries[index].HasHit ? hit.collider.gameObject : null;
            if (entries[index].HasHit)
            {
                Transform t = hit.collider.transform;
                entries[index].HitTransform = t;
                entries[index].HitPosition = t.position;
                entries[index].HitRotation = t.rotation;
            }
            else entries[index].HitTransform = null;
        }

        static float Hint(int mask, float distance)
        {
            for (int i = 0; i < hintCount; i++)
            {
                if (hintMasks[i] != mask) continue;
                if (distance > hintDistances[i]) hintDistances[i] = Mathf.Min(distance, 10f);
                return hintDistances[i];
            }
            if (hintCount < MaxMasks)
            {
                hintMasks[hintCount] = mask;
                hintDistances[hintCount] = Mathf.Min(distance, 10f);
                hintCount++;
            }
            return distance;
        }

        // ----- Patches -----------------------------------------------------------------------------------------
        // ActionHelpers.DoMousePick (PlayMaker 1.6):
        //   if (!(Camera.main == null)) {
        //     Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        //     Physics.Raycast(ray, out mousePickInfo, distance, layerMask);
        //     mousePickLayerMaskUsed = layerMask; mousePickDistanceUsed = distance; mousePickRaycastTime = Time.frameCount;
        //   }

        static bool PickPrefix(float distance, int layerMask, ref float ___mousePickRaycastTime, ref float ___mousePickDistanceUsed, ref int ___mousePickLayerMaskUsed)
        {
            if (!Instance.Live) return true;
            try
            {
                RaycastHit hit;
                int r = Pick(distance, layerMask, out hit);
                if (r == PickNoCamera) return false;             // the original does nothing either
                if (r == PickHit) ActionHelpers.mousePickInfo = hit;
                else clearCollider(ref ActionHelpers.mousePickInfo);
                ___mousePickLayerMaskUsed = layerMask;
                ___mousePickDistanceUsed = distance;
                ___mousePickRaycastTime = Time.frameCount;
                return false;
            }
            catch (Exception ex)
            {
                Instance.Fail("DoMousePick", ex);
                return true;
            }
        }

        static bool armed;
        static RaycastHit expected;
        static float expectedDistance;
        static int expectedMask;

        static bool PickPrefixVerify(float distance, int layerMask)
        {
            armed = false;
            if (!Instance.Live) return true;
            try
            {
                int r = Pick(distance, layerMask, out expected);
                if (r != PickNoCamera)
                {
                    if (r == PickMiss)
                    {
                        expected = ActionHelpers.mousePickInfo;
                        clearCollider(ref expected);
                    }
                    armed = true;
                    expectedDistance = distance;
                    expectedMask = layerMask;
                }
            }
            catch (Exception ex)
            {
                armed = false;
                Instance.Fail("DoMousePick/verify", ex);
            }
            return true;
        }

        static void PickPostfixVerify(float distance, int layerMask)
        {
            if (!armed) return;
            armed = false;
            if (distance != expectedDistance || layerMask != expectedMask) return;
            Checked++;
            RaycastHit a = ActionHelpers.mousePickInfo;
            if (SameHit(a, expected)) return;
            Mismatched++;
            lastMismatch = "mask " + layerMask + ", " + distance + " m: original " + Describe(a) + ", shared " + Describe(expected);
        }

        /// <summary>Every field of the struct (barycentricCoordinate is computed from them).</summary>
        static bool SameHit(RaycastHit a, RaycastHit b)
        {
            Vector3 pa = a.point, pb = b.point, na = a.normal, nb = b.normal, ba = a.barycentricCoordinate, bb = b.barycentricCoordinate;
            return ReferenceEquals(a.collider, b.collider) && a.distance.Equals(b.distance) && a.triangleIndex == b.triangleIndex &&
                   pa.x.Equals(pb.x) && pa.y.Equals(pb.y) && pa.z.Equals(pb.z) &&
                   na.x.Equals(nb.x) && na.y.Equals(nb.y) && na.z.Equals(nb.z) &&
                   ba.x.Equals(bb.x) && ba.y.Equals(bb.y) && ba.z.Equals(bb.z);
        }

        static string Describe(RaycastHit h) =>
            (h.collider != null ? h.collider.name : "nothing") + " d=" + h.distance.ToString("R") + " tri=" + h.triangleIndex;

        public override long Mismatches => Mismatched;

        public override void Report(StringBuilder sb)
        {
            double perQuery = Queries > 0 ? Raycasts / (double)Queries : 0;
            sb.Append("  ").Append(Queries).Append(" picks, ").Append(Raycasts).Append(" raycasts (").Append(perQuery.ToString("0.00"))
              .Append(" per pick), ").Append(CameraFlushes).Append(" camera moves, ").Append(StaleHits).Append(" stale hits");
            if (verify) sb.Append(", checked ").Append(Checked).Append(", mismatches ").Append(Mismatched).Append(lastMismatch != null ? " (last: " + lastMismatch + ")" : "");
            sb.Append('\n');
        }
    }
}
