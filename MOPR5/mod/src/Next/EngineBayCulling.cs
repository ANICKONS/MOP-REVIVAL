// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Engine bay hidden from the driver's seat (5.1, EXPERIMENTAL, off by default).
//
// The idea of SatsumaFpsOptimization (hide engine parts while driving), made conditional and per camera:
//   * only while the player sits in the Satsuma (the main camera is inside the cabin) and the hood is attached,
//     drawn and closed: then the hood, the firewall and the dashboard cover the engine completely;
//   * only for the MAIN camera, and only during its own render: right before it culls, the engine objects
//     (everything under CarMotorPivot that has a mesh renderer, no collider and casts no shadow) are moved to a
//     layer the main camera does not draw, right after it rendered they get their layers back. Mirrors, reflection probes and
//     every other camera draw the engine as usual, and no game code sees a changed layer (physics, raycasts and
//     saving all happen outside that window).
// Why a layer and not Renderer.enabled: switching ~140 renderers off and on around every frame costs more
// than drawing them in Unity 5.0 (measured +0.5 ms); keeping them off is not possible either, because the
// game's realtime reflection probe camera also renders the scene and could see the engine from below.
// Measured (TestBot, 1 s A/B blocks): 0 ms seated in Peraejaervi, -1.1 ms while driving there; the reference
// of simply switching the engine renderers off gave -0.1...-0.5 ms in different runs. Noisy, hence measured
// again by every user who turns it on.
//
// Why experimental: "the hood covers it" is an assumption about the car model, not something MOP can check
// per pixel, and Renderer.isVisible of engine parts reads false while the player drives.

using System;
using System.Collections.Generic;
using UnityEngine;

using MOP.Common;
using MOP.Vehicles.Cases;

namespace MOP.Next
{
    internal static class EngineBayCulling
    {
        const float RefreshSeconds = 5f;        // parts can be added or removed: collect them again

        static bool installed;
        static Transform car, motor, pivotHood, player;
        static readonly List<GameObject> parts = new List<GameObject>();
        static readonly List<int> savedLayers = new List<int>();
        static readonly List<GameObject> moved = new List<GameObject>();
        static float nextRefresh;
        static Camera hiddenFor;
        static int checkedFrame = -1, hiddenFrame = -1;
        static Camera mainThisFrame;

        public static bool Installed => installed;

        /// <summary>A/B measurement only (TestBot): while true, the engine is drawn as usual.</summary>
        public static bool Bypass { get; set; }

        public static long FramesHidden, PartsHidden;
        static string lastReason = "-";
        static int lastLayer = -1;

        public static void Install()
        {
            if (installed) return;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            installed = true;
        }

        public static void Remove()
        {
            if (!installed) return;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            ClearCaches();
            installed = false;
        }

        public static void ResetStats() => FramesHidden = PartsHidden = 0;

        public static void ClearCaches()
        {
            Restore();
            car = motor = pivotHood = player = null;
            parts.Clear();
            nextRefresh = 0f;
            checkedFrame = -1;
        }

        /// <summary>Before saving: nothing is ever changed outside the main camera's render, but be sure.</summary>
        public static void OnPreSave() => Restore();

        static bool Resolve()
        {
            if (car != null && motor != null && pivotHood != null && player != null) return true;
            Satsuma s = Satsuma.Instance;
            if (s == null || s.IsPassive || s.gameObject == null) return false;
            car = s.transform;
            motor = car.Find("Chassis/sub frame(xxxxx)/CarMotorPivot");
            pivotHood = car.Find("Body/pivot_hood");
            player = Hypervisor.Instance != null ? Hypervisor.Instance.GetPlayer() : null;
            parts.Clear();
            nextRefresh = 0f;
            return motor != null && pivotHood != null && player != null;
        }

        /// <summary>The player sits in the car and the hood is attached, drawn and closed.</summary>
        static bool EngineCovered()
        {
            if (!MopSettings.IsModActive || !Resolve()) { lastReason = "no Satsuma"; return false; }
            if (player.root != car) { lastReason = "player not in the car"; return false; }
            if (pivotHood.childCount == 0) { lastReason = "no hood"; return false; }
            Transform hood = pivotHood.GetChild(0);
            if (!hood.gameObject.activeInHierarchy) { lastReason = "hood hidden"; return false; }
            Renderer hoodRenderer = hood.GetComponent<Renderer>();
            if (hoodRenderer == null || !hoodRenderer.enabled) { lastReason = "hood not drawn"; return false; }
            // Closed = resting on its attachment pose (measured: local rotation (0, 0, 0) when closed; the hinge
            // opens it around the local X axis by tens of degrees).
            if (Quaternion.Angle(hood.localRotation, Quaternion.identity) >= 1f) { lastReason = "hood open"; return false; }
            lastReason = "engine covered";
            return true;
        }

        /// <summary>A layer the camera does not draw (the highest free one), or -1.</summary>
        static int HiddenLayer(Camera cam)
        {
            int mask = cam.cullingMask;
            for (int l = 31; l >= 8; l--)
                if ((mask & (1 << l)) == 0) return l;
            return -1;
        }

        static void Refresh()
        {
            parts.Clear();
            // Solid parts only (no smoke or other effects), and never an object with a collider: its layer is
            // part of the physics and raycast state.
            foreach (Renderer r in motor.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.GetComponent<Collider>() != null) continue;
                // A shadow caster stays: the main camera's shadow pass would lose its shadow (the stock engine has none).
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) continue;
                parts.Add(r.gameObject);
            }
            nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
        }

        static void OnPreCull(Camera cam)
        {
            try
            {
                if (cam == null) return;
                int f = Time.frameCount;
                if (f != checkedFrame)
                {
                    checkedFrame = f;
                    mainThisFrame = Camera.main;
                    if (hiddenFor != null) Restore();   // safety net: the main camera did not finish its render
                }
                if (cam != mainThisFrame || Bypass || hiddenFor != null) return;
                if (!EngineCovered()) return;
                int layer = HiddenLayer(cam);
                if (layer < 0) { lastReason = "no free layer"; return; }
                lastLayer = layer;
                if (Time.realtimeSinceStartup >= nextRefresh) Refresh();

                for (int i = 0; i < parts.Count; i++)
                {
                    GameObject go = parts[i];
                    if (go == null || !go.activeInHierarchy) continue;
                    savedLayers.Add(go.layer);
                    moved.Add(go);
                    go.layer = layer;
                }
                hiddenFor = cam;
                hiddenFrame = f;
                FramesHidden++;
                PartsHidden += moved.Count;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "ENGINE_BAY_CULLING");
                Restore();
                Bypass = true;
            }
        }

        static void OnPostRender(Camera cam)
        {
            if (hiddenFor == null || cam != hiddenFor) return;
            Restore();
        }

        /// <summary>Every engine object gets its own layer back.</summary>
        static void Restore()
        {
            for (int i = 0; i < moved.Count; i++)
                if (moved[i] != null) moved[i].layer = savedLayers[i];
            moved.Clear();
            savedLayers.Clear();
            hiddenFor = null;
        }

        public static string Report()
        {
            if (!installed) return "Engine bay hidden from the driver's seat (experimental): off";
            return $"Engine bay hidden from the driver's seat (experimental): ON{(Bypass ? " (bypassed)" : "")}, {parts.Count} engine objects, " +
                   $"hidden in {FramesHidden} frames ({(FramesHidden > 0 ? PartsHidden / (double)FramesHidden : 0):0} per frame, layer {lastLayer}), now: {lastReason}";
        }
    }
}
