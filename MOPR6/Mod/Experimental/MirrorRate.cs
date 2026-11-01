// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// experimental.mirrors (task 35, requirement 7.5; ported from MOPR 5.x Next/MirrorRate.cs): car mirrors at most
// 60 times per second. EXPERIMENTAL, off by default.
//
// While driving, the game renders the scene two more times per frame for the rear-view and side mirrors
// (Systems/Mirrors: RearViewMirrorCam 256x256, LeftSideMirrorCam 256x128). Measured in 5.x from the driver's seat:
// 0.4 ms per frame in Peraejaervi, 1.2 ms in the home garage. Above 60 FPS a mirror render is skipped when the
// mirror was rendered less than 1/60 s ago: right before the mirror camera culls, its culling mask is set to nothing
// and its clear flags to "don't clear" (the mirror keeps its picture), right after its empty render both are put
// back. The camera is never disabled; outside that mirror's own render nothing is different, so no game code ever
// sees it - which is also why this does not go through the ledger (nothing is held between frames).
// Why experimental: on a skipped frame the mirror shows the picture of the previous frame (8 ms old at 120 FPS).

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Experimental
{
    public sealed class MirrorRate : Optimization, Render.IAbSwitch
    {
        public static readonly MirrorRate Instance = new MirrorRate();
        public const string SystemId = "experimental.mirrors";
        const float MinInterval = 1f / 60f;

        sealed class Skipped
        {
            public int Mask;
            public CameraClearFlags Clear;
        }

        readonly Dictionary<Camera, float> lastRender = new Dictionary<Camera, float>();
        readonly Dictionary<Camera, Skipped> skipping = new Dictionary<Camera, Skipped>();
        bool hooked, bypass;
        long rendered, skippedRenders;

        MirrorRate() : base(SystemId) { }

        public override string Title => "Mirrors at most 60 FPS";
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
            RestoreAll();
            lastRender.Clear();
        }

        protected internal override void OnScene()
        {
            RestoreAll();
            lastRender.Clear();
        }

        public string SetAb(bool on)
        {
            bypass = !on;
            if (bypass) RestoreAll();
            return null;
        }

        static bool IsMirror(Camera cam) => cam.targetTexture != null && cam.name.Contains("MirrorCam");

        void OnPreCull(Camera cam)
        {
            if (!Live || cam == null) return;
            try
            {
                if (!IsMirror(cam)) return;
                float now = Time.realtimeSinceStartup;
                float last;
                bool seen = lastRender.TryGetValue(cam, out last);
                if (bypass || !seen || now - last >= MinInterval || now < last)
                {
                    lastRender[cam] = now;
                    rendered++;
                    return;
                }
                if (!skipping.ContainsKey(cam)) skipping[cam] = new Skipped { Mask = cam.cullingMask, Clear = cam.clearFlags };
                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Nothing;
                skippedRenders++;
            }
            catch (Exception ex)
            {
                RestoreAll();
                Fail("OnPreCull", ex, cam != null ? cam.name : null);
            }
        }

        void OnPostRender(Camera cam)
        {
            if (cam == null || skipping.Count == 0) return;
            Skipped s;
            if (!skipping.TryGetValue(cam, out s)) return;
            cam.cullingMask = s.Mask;
            cam.clearFlags = s.Clear;
            skipping.Remove(cam);
        }

        void RestoreAll()
        {
            foreach (KeyValuePair<Camera, Skipped> kv in skipping)
            {
                if (kv.Key == null) continue;
                kv.Key.cullingMask = kv.Value.Mask;
                kv.Key.clearFlags = kv.Value.Clear;
            }
            skipping.Clear();
        }

        public override void Report(StringBuilder sb)
        {
            long total = rendered + skippedRenders;
            sb.Append("  ").Append(rendered).Append(" mirror renders, ").Append(skippedRenders).Append(" skipped (")
              .Append(total > 0 ? (100.0 * skippedRenders / total).ToString("0") : "0").Append("%)").Append(bypass ? ", A/B: bypassed" : "").Append('\n');
        }
    }
}
