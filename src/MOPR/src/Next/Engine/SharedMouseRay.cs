// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Shared mouse ray (5.1, EXPERIMENTAL, off by default).
//
// Every part of the Satsuma has a "Removal" state machine with a MousePickEvent action that asks, every frame,
// "is the mouse over me?". PlayMaker answers with ActionHelpers.MouseOver/IsMouseOver/MousePick, which cast a ray
// from Camera.main. PlayMaker caches that ray for one frame, but only for ONE layer mask: the game interleaves
// about a dozen masks, so the cache almost never hits. Measured with the TestBot: 300 queries and 107 real
// raycasts per frame next to the car, 181 raycasts per frame while seated in it in the village (2 ms per frame).
// Each raycast also looks up Camera.main (a tag search in Unity 5.0) twice.
//
// This patch keeps one ray per layer mask per frame. Within a frame, a query is answered from the ray of its
// mask when that ray is at least as long; the answer is the one a fresh raycast with exactly this distance would
// give (the closest hit, if it is closer than the distance). The cache is dropped as soon as the camera moved,
// and an entry is cast again when the collider it hit was disabled or deactivated since.
//
// Why experimental: a fresh raycast would also see a collider that moved into or out of the ray during the same
// frame (between two FSM updates). The answer can then be one frame late for some state machines. The verify mode
// (Engine Lab verify setting) runs the original as well and counts every different answer.

using System;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

using MOP.Common;

namespace MOP.Next.Engine
{
    static class SharedMouseRay
    {
        const string HarmonyId = "com.anickon.mopr.engine.mouseray";
        const int MaxMasks = 32;

        struct Entry
        {
            public int Mask;
            public float Distance;          // length of the cast ray
            public bool HasHit;
            public RaycastHit Hit;
            public Collider Collider;
            public GameObject GameObject;
        }

        static HarmonyInstance harmony;

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
        static Ray ray;

        public static bool Installed => harmony != null;
        public static bool Shadow { get; private set; }

        /// <summary>A/B measurement only (TestBot): while true, the original PlayMaker code answers.</summary>
        public static bool Bypass { get; set; }

        public static long Queries, Raycasts, CameraFlushes, StaleHits, ShadowChecked, ShadowMismatch;
        static string lastMismatch;

        public static string Install(bool shadow)
        {
            Remove();
            try
            {
                Type helpers = typeof(ActionHelpers);
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public;
                FieldInfo pickInfo = helpers.GetField("mousePickInfo", S);
                MethodInfo mouseOver = helpers.GetMethod("MouseOver", S, null, new[] { typeof(float), typeof(int) }, null);
                MethodInfo mousePick = helpers.GetMethod("MousePick", S, null, new[] { typeof(float), typeof(int) }, null);
                MethodInfo isMouseOver = helpers.GetMethod("IsMouseOver", S, null, new[] { typeof(GameObject), typeof(float), typeof(int) }, null);
                if (pickInfo == null || pickInfo.FieldType != typeof(RaycastHit) || mouseOver == null || mousePick == null || isMouseOver == null)
                    return "PlayMaker ActionHelpers changed";

                Shadow = shadow;
                harmony = HarmonyInstance.Create(HarmonyId);
                harmony.Patch(isMouseOver, Hook("IsMouseOverPrefix"), shadow ? Hook("IsMouseOverPostfix") : null, null);
                harmony.Patch(mouseOver, Hook("MouseOverPrefix"), shadow ? Hook("MouseOverPostfix") : null, null);
                harmony.Patch(mousePick, Hook("MousePickPrefix"), null, null);
                frame = -1;
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Remove()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            frame = -1;
            entryCount = 0;
            camera = null;
            cameraTransform = null;
            for (int i = 0; i < entries.Length; i++) entries[i] = default(Entry);
        }

        public static void ResetStats()
        {
            Queries = Raycasts = CameraFlushes = StaleHits = ShadowChecked = ShadowMismatch = 0;
            lastMismatch = null;
        }

        /// <summary>Scene change: no references into the old scene.</summary>
        public static void ClearCaches()
        {
            frame = -1;
            entryCount = 0;
            camera = null;
            cameraTransform = null;
            for (int i = 0; i < entries.Length; i++) entries[i] = default(Entry);
        }

        static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(SharedMouseRay).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ----- The cache ---------------------------------------------------------------------------------

        /// <summary>
        /// The ray entry for this mask, valid for a query of this distance. False if there is no camera (the
        /// original code then runs, it keeps the previous result in that case).
        /// </summary>
        static bool Lookup(float distance, int layerMask, out int index)
        {
            index = -1;
            int f = Time.frameCount;
            if (f != frame)
            {
                frame = f;
                entryCount = 0;
                camera = Camera.main;
                cameraTransform = camera != null ? camera.transform : null;
                if (cameraTransform != null) Aim();
            }
            if (cameraTransform == null) return false;

            // The camera moved since the rays were cast (mouse look runs between state machines): cast again.
            Vector3 p = cameraTransform.position;
            Quaternion q = cameraTransform.rotation;
            if (p.x != cameraPosition.x || p.y != cameraPosition.y || p.z != cameraPosition.z ||
                q.x != cameraRotation.x || q.y != cameraRotation.y || q.z != cameraRotation.z || q.w != cameraRotation.w)
            {
                CameraFlushes++;
                entryCount = 0;
                Aim();
            }

            for (int i = 0; i < entryCount; i++)
            {
                if (entries[i].Mask != layerMask) continue;
                if (entries[i].Distance >= distance && !IsStale(ref entries[i]))
                {
                    index = i;
                    return true;
                }
                Cast(i, distance, layerMask);
                index = i;
                return true;
            }

            if (entryCount >= MaxMasks) entryCount = 0;   // never happens in practice (about 12 masks)
            index = entryCount++;
            Cast(index, distance, layerMask);
            return true;
        }

        static void Aim()
        {
            cameraPosition = cameraTransform.position;
            cameraRotation = cameraTransform.rotation;
            ray = camera.ScreenPointToRay(Input.mousePosition);
        }

        /// <summary>The collider that was hit is gone or switched off: a fresh ray would not hit it.</summary>
        static bool IsStale(ref Entry e)
        {
            if (!e.HasHit) return false;
            if (e.Collider == null || !e.Collider.enabled || !e.GameObject.activeInHierarchy)
            {
                StaleHits++;
                return true;
            }
            return false;
        }

        static void Cast(int index, float distance, int layerMask)
        {
            // Cast as far as this mask was ever asked for, so the longer queries of this frame hit the cache too.
            float length = Mathf.Max(distance, Hint(layerMask, distance));
            RaycastHit hit;
            bool any = Physics.Raycast(ray, out hit, length, layerMask);
            Raycasts++;
            entries[index].Mask = layerMask;
            entries[index].Distance = length;
            entries[index].HasHit = any && hit.collider != null;
            entries[index].Hit = hit;
            entries[index].Collider = entries[index].HasHit ? hit.collider : null;
            entries[index].GameObject = entries[index].HasHit ? hit.collider.gameObject : null;
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

        /// <summary>What a fresh Physics.Raycast(ray, out hit, distance, mask) would have stored.</summary>
        static RaycastHit FreshHit(ref Entry e, float distance)
        {
            return e.HasHit && e.Hit.distance <= distance ? e.Hit : default(RaycastHit);
        }

        /// <summary>ActionHelpers.MouseOver: the collider's object if it is closer than the distance.</summary>
        static GameObject Answer(ref Entry e, float distance)
        {
            return e.HasHit && e.Hit.distance < distance ? e.GameObject : null;
        }

        // ----- Patches -----------------------------------------------------------------------------------
        // Each prefix also writes ActionHelpers.mousePickInfo, as the original does: MousePickEvent copies it
        // into Fsm.RaycastHitInfo right after the call.

        static bool IsMouseOverPrefix(GameObject gameObject, float distance, int layerMask, ref bool __result)
        {
            if (Bypass) return true;
            try
            {
                // The original returns before casting when the object is null (or destroyed).
                if (gameObject == null) { __result = false; expectedBool = false; return Shadow; }
                if (!Lookup(distance, layerMask, out int i)) return true;
                Queries++;
                ActionHelpers.mousePickInfo = FreshHit(ref entries[i], distance);
                GameObject over = Answer(ref entries[i], distance);
                __result = over != null && ReferenceEquals(over, gameObject);
                if (Shadow) { expectedBool = __result; return true; }
                return false;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return true;
            }
        }

        static bool expectedBool;
        static GameObject expectedObject;

        static void IsMouseOverPostfix(GameObject gameObject, bool __result)
        {
            if (Bypass) return;
            ShadowChecked++;
            if (__result != expectedBool)
            {
                ShadowMismatch++;
                lastMismatch = "IsMouseOver " + (gameObject != null ? gameObject.name : "null") + ": original " + __result;
            }
        }

        static bool MouseOverPrefix(float distance, int layerMask, ref GameObject __result)
        {
            if (Bypass) return true;
            try
            {
                if (!Lookup(distance, layerMask, out int i)) return true;
                Queries++;
                ActionHelpers.mousePickInfo = FreshHit(ref entries[i], distance);
                __result = Answer(ref entries[i], distance);
                if (Shadow) { expectedObject = __result; return true; }
                return false;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return true;
            }
        }

        static void MouseOverPostfix(GameObject __result)
        {
            if (Bypass) return;
            ShadowChecked++;
            if (!ReferenceEquals(__result, expectedObject))
            {
                ShadowMismatch++;
                lastMismatch = "MouseOver: original " + (__result != null ? __result.name : "null") +
                               ", shared " + (expectedObject != null ? expectedObject.name : "null");
            }
        }

        static bool MousePickPrefix(float distance, int layerMask, ref RaycastHit __result)
        {
            if (Bypass || Shadow) return true;
            try
            {
                if (!Lookup(distance, layerMask, out int i)) return true;
                Queries++;
                __result = FreshHit(ref entries[i], distance);
                ActionHelpers.mousePickInfo = __result;
                return false;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return true;
            }
        }

        static void Fail(Exception ex)
        {
            ExceptionManager.New(ex, false, "SHARED_MOUSE_RAY");
            Bypass = true;      // the original code answers from now on
        }

        public static void AppendReport(StringBuilder sb)
        {
            if (!Installed) { sb.AppendLine("Shared mouse ray (experimental): off"); return; }
            double perQuery = Queries > 0 ? Raycasts / (double)Queries : 0;
            sb.AppendLine($"Shared mouse ray (experimental): {(Shadow ? "VERIFY" : "ON")}{(Bypass ? " (bypassed)" : "")}, " +
                          $"{Queries} queries, {Raycasts} raycasts ({perQuery:0.00} per query), {CameraFlushes} camera moves, {StaleHits} stale hits");
            if (Shadow)
                sb.AppendLine($"  verify: {ShadowChecked} answers checked, {ShadowMismatch} different" +
                              (lastMismatch != null ? " (last: " + lastMismatch + ")" : ""));
        }
    }
}
