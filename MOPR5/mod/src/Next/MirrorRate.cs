// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Mirrors at most 60 times per second (5.1, EXPERIMENTAL, off by default).
//
// While driving, the game renders the scene two more times per frame for the rear-view and side mirrors
// (Systems/Mirrors: RearViewMirrorCam 256x256, LeftSideMirrorCam 256x128, 200 m far plane). Measured with the
// TestBot from the Satsuma's driver seat: the mirror cameras cost 0.4 ms per frame in Peraejaervi and 1.2 ms in
// the home garage (they see the whole house behind the car).
//
// Above 60 FPS this skips a mirror render when the mirror was rendered less than 1/60 s ago. A skipped mirror
// keeps its picture from the previous frame: right before the mirror camera culls, its culling mask is set to
// nothing and its clear flags to "don't clear", and right after its (empty) render both are put back. The
// camera is never disabled and nothing else is changed, so the game never sees a different camera state.
// Below 60 FPS every mirror render happens as usual.
//
// Why experimental: on a skipped frame the mirror shows the picture of the previous frame (8 ms old at 120 FPS).

using System;
using System.Collections.Generic;
using UnityEngine;

using MOP.Common;

namespace MOP.Next
{
    internal static class MirrorRate
    {
        const float MinInterval = 1f / 60f;

        sealed class Skipped
        {
            public int Mask;
            public CameraClearFlags Clear;
        }

        static bool installed;
        static readonly Dictionary<Camera, float> lastRender = new Dictionary<Camera, float>();
        static readonly Dictionary<Camera, Skipped> skipping = new Dictionary<Camera, Skipped>();

        public static bool Installed => installed;

        /// <summary>A/B measurement only (TestBot): while true, every mirror render happens.</summary>
        public static bool Bypass { get; set; }

        public static long Rendered, SkippedRenders;

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
            RestoreAll();
            lastRender.Clear();
            installed = false;
        }

        public static void ResetStats() => Rendered = SkippedRenders = 0;

        static bool IsMirror(Camera cam) => cam.targetTexture != null && cam.name.Contains("MirrorCam");

        static void OnPreCull(Camera cam)
        {
            try
            {
                if (cam == null || !IsMirror(cam)) return;
                float now = Time.realtimeSinceStartup;
                float last;
                bool seen = lastRender.TryGetValue(cam, out last);
                if (Bypass || !seen || now - last >= MinInterval || now < last)
                {
                    lastRender[cam] = now;
                    Rendered++;
                    return;
                }

                // Keep the previous picture: cull nothing, clear nothing. Restored in OnPostRender.
                if (!skipping.ContainsKey(cam))
                    skipping[cam] = new Skipped { Mask = cam.cullingMask, Clear = cam.clearFlags };
                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Nothing;
                SkippedRenders++;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "MIRROR_RATE_PRECULL");
                Bypass = true;
                RestoreAll();
            }
        }

        static void OnPostRender(Camera cam)
        {
            if (cam == null || skipping.Count == 0) return;
            Skipped s;
            if (!skipping.TryGetValue(cam, out s)) return;
            cam.cullingMask = s.Mask;
            cam.clearFlags = s.Clear;
            skipping.Remove(cam);
        }

        static void RestoreAll()
        {
            foreach (KeyValuePair<Camera, Skipped> kv in skipping)
            {
                if (kv.Key == null) continue;
                kv.Key.cullingMask = kv.Value.Mask;
                kv.Key.clearFlags = kv.Value.Clear;
            }
            skipping.Clear();
        }

        /// <summary>Scene change: no references into the old scene.</summary>
        public static void ClearCaches()
        {
            RestoreAll();
            lastRender.Clear();
        }

        public static string Report()
        {
            if (!installed) return "Mirrors at most 60 FPS (experimental): off";
            long total = Rendered + SkippedRenders;
            return $"Mirrors at most 60 FPS (experimental): ON{(Bypass ? " (bypassed)" : "")}, {Rendered} mirror renders, " +
                   $"{SkippedRenders} skipped ({(total > 0 ? 100.0 * SkippedRenders / total : 0):0}%)";
        }
    }
}
