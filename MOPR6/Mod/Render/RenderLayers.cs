// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// render.layers (requirement 9, design §8; ported from the layer culling of MOPR 5.x Next/RenderOptimizer.cs): per
// layer, the main camera stops drawing objects that would be smaller than 2 px on screen. Unity culls by layer
// (Camera.layerCullDistances), so the distance of a layer comes from the LARGEST object on it: size x pixels per
// metre / 2 px, never closer than 30 m; a layer whose distance would be beyond 4 000 m is left alone.
//   * Sizes from every renderer Unity knows (active, inactive, prefabs: only larger, never smaller), again 60 s after
//     the load for objects that appear later. A layer with renderers of unknown size (particles, lines, sprites), the
//     UI layer and every layer with a renderer whose visibility the game reads (RenderScan) is never culled.
//   * Distances are the camera's own ones where it has them (min of both), spherical culling (no popping at the
//     screen edges when turning). Recomputed when the field of view, the screen height or the camera change.
//   * Through the camera cull arbiter (value arbiter: CameraLayerCull) and the spherical arbiter; the ledger gives the
//     camera's own values back at the save barrier, with the main switch and when the system goes. No game script of
//     MSC writes these distances (LODcam / MainCamera are not in the scene); if something does, its values win and
//     become the new originals - after 3 such take-overs in a session the camera is left to it.

using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Diagnostics;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;

namespace MOPR6.Render
{
    public sealed class RenderLayers : Optimization
    {
        public static readonly RenderLayers Instance = new RenderLayers();
        public const string SystemId = "render.layers";

        const float CullPixels = 2f;
        const float MinDistance = 30f;
        const float MaxUseful = 4000f;
        const double CheckSeconds = 0.5, LateScanSeconds = 60;
        const int MaxTakeOvers = 3;

        readonly float[] maxSize = new float[RenderScan.LayerCount];
        readonly bool[] cullable = new bool[RenderScan.LayerCount];
        readonly int[] count = new int[RenderScan.LayerCount];
        bool scanned, lateScanDone, statsChanged;
        double installedAt, nextCheck;

        Camera camera;
        int cameraId;
        bool holding;
        float lastPpm = -1f;
        int takeOvers;
        readonly HashSet<int> givenUp = new HashSet<int>();
        float[] applied;
        string lastLayers = "";

        RenderLayers() : base(SystemId)
        {
            Arbiters.ValueDropped += (key, system) => { if (system == Id) takeOvers++; };
        }

        public override string Title => "Layer cull distances";
        public override Tier Tier => Tier.Render;
        // Off by default: no measurable gain in vanilla (render-quick / merge-quick, 2026-10-29; Docs/render.ru.md).
        public override bool DefaultOn => false;

        // The layer statistics are kept while the scene lives (switching the system off and on costs nothing); the
        // camera's distances go back through the arbiter when the system is removed.
        protected internal override string OnInstall(SystemMode mode)
        {
            ResetCamera();
            if (!scanned) installedAt = ModLog.Now();
            return null;
        }

        protected internal override void OnRemove() { ResetCamera(); }

        protected internal override void OnScene()
        {
            scanned = lateScanDone = statsChanged = false;
            takeOvers = 0;
            givenUp.Clear();
            ResetCamera();
            installedAt = ModLog.Now();
        }

        void ResetCamera()
        {
            holding = false;
            camera = null;
            cameraId = 0;
            lastPpm = -1f;
            applied = null;
            nextCheck = 0;
        }

        protected internal override void OnTick(double now)
        {
            if (now < nextCheck) return;
            nextCheck = now + CheckSeconds;
            if (!scanned) { ScanAll(); scanned = true; }
            else if (!lateScanDone && now - installedAt >= LateScanSeconds) { ScanActive(); lateScanDone = true; }

            Camera cam = Camera.main;
            if (cam != camera)
            {
                Release();
                camera = cam;
                cameraId = cam != null ? Arbiters.Id(cam) : 0;
                lastPpm = -1f;
            }
            if (cam == null || givenUp.Contains(cameraId)) return;

            if (holding && Arbiters.CameraCull.Holder(cameraId) != Id)
            {
                // Somebody else wrote the distances (OnDropped counts it) or everything was restored (save barrier,
                // restore): compute again from the camera's values as they are now.
                holding = false;
                applied = null;
            }
            if (takeOvers >= MaxTakeOvers && givenUp.Add(cameraId))
            {
                ModLog.Line(Id + ": " + cam.name + " has its own layer distances, left to it");
                return;
            }

            float ppm = RenderScan.PixelsPerMeter(cam);
            if (holding && !statsChanged && Mathf.Approximately(ppm, lastPpm)) return;
            lastPpm = ppm;
            statsChanged = false;

            PropValue? held = Arbiters.CameraCull.Original(cameraId);
            float[] original = held.HasValue ? held.Value.A : cam.layerCullDistances;
            float[] d = Distances(original, ppm, out string layers);
            if (d == null)
            {
                Release();
                return;
            }
            if (applied != null && Same(applied, d)) return;
            if (Arbiters.CameraCull.Set(cameraId, Id, PropValue.Floats(d)) != SetResult.Ok) return;
            Arbiters.Spherical.Add(cameraId, Id);
            holding = true;
            applied = d;
            if (layers != lastLayers)
            {
                lastLayers = layers;
                ModLog.Line(Id + ": " + cam.name + " " + layers);
            }
        }

        void Release()
        {
            if (cameraId != 0 && Arbiters.CameraCull != null)
            {
                Arbiters.CameraCull.Remove(cameraId, Id);
                Arbiters.Spherical.Remove(cameraId, Id);
            }
            holding = false;
            applied = null;
        }

        /// <summary>The distances to apply, or null when no layer gets one.</summary>
        float[] Distances(float[] original, float ppm, out string layers)
        {
            float[] d = new float[RenderScan.LayerCount];
            StringBuilder sb = new StringBuilder();
            bool any = false;
            for (int i = 0; i < RenderScan.LayerCount; i++)
            {
                float result = original != null && i < original.Length ? original[i] : 0f;
                if (cullable[i] && count[i] > 0 && maxSize[i] > 0f && !RenderScan.LayerObserved(i))
                {
                    float c = Mathf.Max(MinDistance, maxSize[i] * ppm / CullPixels);
                    if (c < MaxUseful)
                    {
                        result = result > 0f ? Mathf.Min(result, c) : c;
                        any = true;
                        sb.Append(i).Append(':').Append(Mathf.RoundToInt(result)).Append("m ");
                    }
                }
                d[i] = result;
            }
            layers = sb.ToString().TrimEnd();
            return any ? d : null;
        }

        static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (Mathf.Abs(a[i] - b[i]) > 0.01f) return false;
            return true;
        }

        void ScanAll()
        {
            for (int i = 0; i < RenderScan.LayerCount; i++)
            {
                maxSize[i] = 0f;
                cullable[i] = i != RenderScan.UiLayer;
                count[i] = 0;
            }
            foreach (Renderer r in Resources.FindObjectsOfTypeAll<Renderer>()) Feed(r);
            statsChanged = true;
        }

        void ScanActive()
        {
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>()) Feed(r);
        }

        void Feed(Renderer r)
        {
            if (r == null || RenderScan.IsOurs(r.gameObject)) return;
            int layer = r.gameObject.layer;
            if (layer < 0 || layer >= RenderScan.LayerCount || !cullable[layer]) return;
            if (count[layer]++ == 0) statsChanged = true;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
            {
                // Particles, lines, trails, sprites: their size is not knowable - never cull that layer.
                cullable[layer] = false;
                statsChanged = true;
                return;
            }
            float size;
            try { size = RenderScan.Size(r); }
            catch (System.Exception) { size = -1f; }   // catch-ok: a renderer whose size cannot be read: its layer is never culled
            if (size < 0f) { cullable[layer] = false; statsChanged = true; }
            else if (size > maxSize[layer]) { maxSize[layer] = size; statsChanged = true; }
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  camera ").Append(camera != null ? camera.name : "-").Append(holding ? ", culling " : ", not culling ")
              .Append(lastLayers.Length > 0 ? "layers " + lastLayers : "").Append("; taken over ").Append(takeOvers)
              .Append("; visibility read by the game: ").Append(RenderScan.ObservedSummary).Append('\n');
        }
    }
}
