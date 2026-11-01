// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// experimental.enginebay (task 35, requirement 7.5; ported from MOPR 5.x Next/EngineBayCulling.cs): the engine is not
// drawn for the driver's own view. EXPERIMENTAL, off by default.
//   * Only while the player sits in the Satsuma and the hood is attached, drawn and closed: then the hood, the
//     firewall and the dashboard cover the engine.
//   * Only for the MAIN camera and only during its own render: right before it culls, the engine objects (under
//     CarMotorPivot, with a mesh renderer, without a collider and without a shadow) are moved to a layer the main
//     camera does not draw; right after its render they get their layers back. Mirrors, reflection probes and every
//     other camera draw the engine as usual; physics, raycasts and saving all happen outside that window, so no game
//     code sees a changed layer - which is why this does not go through the ledger (nothing is held between frames).
// Measured in 5.x (1 s A/B blocks): 0 ms seated in Peraejaervi, -1.1 ms while driving there (noisy).
// Why experimental: "the hood covers it" is an assumption about the car model, not checked per pixel, and
// Renderer.isVisible of engine parts reads false while the player drives (no vanilla script reads it: RenderScan).

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Experimental
{
    public sealed class EngineBayCulling : Optimization, Render.IAbSwitch
    {
        public static readonly EngineBayCulling Instance = new EngineBayCulling();
        public const string SystemId = "experimental.enginebay";
        const float RefreshSeconds = 5f;

        Transform car, motor, pivotHood, player;
        readonly List<GameObject> parts = new List<GameObject>();
        readonly List<int> savedLayers = new List<int>();
        readonly List<GameObject> moved = new List<GameObject>();
        float nextRefresh;
        Camera hiddenFor, mainThisFrame;
        int checkedFrame = -1, lastLayer = -1;
        bool hooked, bypass;
        long framesHidden, partsHidden;
        string lastReason = "-";

        EngineBayCulling() : base(SystemId) { }

        public override string Title => "Engine hidden from the driver's seat";
        public override Tier Tier => Tier.Experimental;

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!hooked)
            {
                Camera.onPreCull += OnPreCull;
                Camera.onPostRender += OnPostRender;
                hooked = true;
            }
            return null;
        }

        protected internal override void OnRemove()
        {
            if (hooked)
            {
                Camera.onPreCull -= OnPreCull;
                Camera.onPostRender -= OnPostRender;
                hooked = false;
            }
            Clear();
        }

        protected internal override void OnSuspend(string reason) { Restore(); }

        protected internal override void OnScene() { Clear(); }

        void Clear()
        {
            Restore();
            car = motor = pivotHood = player = null;
            parts.Clear();
            nextRefresh = 0f;
            checkedFrame = -1;
        }

        public string SetAb(bool on)
        {
            bypass = !on;
            if (bypass) Restore();
            return null;
        }

        bool Resolve()
        {
            if (car != null && motor != null && pivotHood != null && player != null) return true;
            GameObject c = GameObject.Find(Render.SatsumaMerge.SatsumaPath);
            GameObject p = GameObject.Find("PLAYER");
            if (c == null || p == null) return false;
            car = c.transform;
            player = p.transform;
            motor = car.Find("Chassis/sub frame(xxxxx)/CarMotorPivot");
            pivotHood = car.Find("Body/pivot_hood");
            parts.Clear();
            nextRefresh = 0f;
            return motor != null && pivotHood != null;
        }

        bool EngineCovered()
        {
            if (!Resolve()) { lastReason = "no Satsuma"; return false; }
            if (player.root != car) { lastReason = "player not in the car"; return false; }
            if (pivotHood.childCount == 0) { lastReason = "no hood"; return false; }
            Transform hood = pivotHood.GetChild(0);
            if (!hood.gameObject.activeInHierarchy) { lastReason = "hood hidden"; return false; }
            Renderer hoodRenderer = hood.GetComponent<Renderer>();
            if (hoodRenderer == null || !hoodRenderer.enabled) { lastReason = "hood not drawn"; return false; }
            // Closed = resting on its attachment pose (local rotation identity; the hinge opens it by tens of degrees).
            if (Quaternion.Angle(hood.localRotation, Quaternion.identity) >= 1f) { lastReason = "hood open"; return false; }
            lastReason = "engine covered";
            return true;
        }

        static int HiddenLayer(Camera cam)
        {
            int mask = cam.cullingMask;
            for (int l = 31; l >= 8; l--)
                if ((mask & (1 << l)) == 0) return l;
            return -1;
        }

        void Refresh()
        {
            parts.Clear();
            foreach (Renderer r in motor.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (r.GetComponent<Collider>() != null) continue;   // its layer is part of the physics and raycast state
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) continue;
                parts.Add(r.gameObject);
            }
            nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
        }

        void OnPreCull(Camera cam)
        {
            if (!Live || cam == null) return;
            try
            {
                int f = Time.frameCount;
                if (f != checkedFrame)
                {
                    checkedFrame = f;
                    mainThisFrame = Camera.main;
                    if (hiddenFor != null) Restore();   // safety net: the main camera did not finish its render
                }
                if (cam != mainThisFrame || bypass || hiddenFor != null) return;
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
                framesHidden++;
                partsHidden += moved.Count;
            }
            catch (Exception ex)
            {
                Restore();
                Fail("OnPreCull", ex, cam != null ? cam.name : null);
            }
        }

        void OnPostRender(Camera cam)
        {
            if (hiddenFor == null || cam != hiddenFor) return;
            Restore();
        }

        void Restore()
        {
            for (int i = 0; i < moved.Count; i++)
                if (moved[i] != null) moved[i].layer = savedLayers[i];
            moved.Clear();
            savedLayers.Clear();
            hiddenFor = null;
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(parts.Count).Append(" engine objects, hidden in ").Append(framesHidden).Append(" frames (")
              .Append(framesHidden > 0 ? (partsHidden / (double)framesHidden).ToString("0") : "0").Append(" per frame, layer ").Append(lastLayer)
              .Append("), now: ").Append(lastReason).Append(bypass ? ", A/B: bypassed" : "").Append('\n');
        }
    }
}
