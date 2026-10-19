// Measures the CPU time cameras spend culling and rendering (OnPreCull -> OnPostRender, which includes
// shadow maps and nested reflection renders, but not image effects or the GUI). Every camera that is
// enabled when the probe starts gets its own probe (mirrors, reflections, render-to-texture cameras).

using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace MOPR.TestBot
{
    class RenderProbe : MonoBehaviour
    {
        static readonly List<RenderProbe> probes = new List<RenderProbe>();
        static RenderProbe mainProbe;

        long ticks;
        int renders;
        long start;
        string cameraName;

        public static bool Begin()
        {
            probes.Clear();
            mainProbe = null;
            Camera main = Camera.main;
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null) continue;
                RenderProbe p = cam.gameObject.AddComponent<RenderProbe>();
                p.cameraName = BotRunner.PathOf(cam.transform) + (cam.targetTexture != null ? " (texture)" : "");
                probes.Add(p);
                if (cam == main) mainProbe = p;
            }
            return mainProbe != null;
        }

        /// <summary>
        /// Returns the average main-camera render ms per frame, fills the other cameras and removes the probes.
        /// </summary>
        public static float End(int frames, out int rendersPerFrameX100, List<ProfileEntry> byCamera, out float otherMs)
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            float main = 0f;
            otherMs = 0f;
            rendersPerFrameX100 = 0;
            foreach (RenderProbe p in probes)
            {
                if (p == null) continue;
                float ms = frames > 0 ? (float)(p.ticks * tickMs / frames) : 0f;
                if (p == mainProbe)
                {
                    main = ms;
                    rendersPerFrameX100 = frames > 0 ? p.renders * 100 / frames : 0;
                }
                else otherMs += ms;
                byCamera?.Add(new ProfileEntry
                {
                    Name = p.cameraName,
                    MsPerFrame = ms,
                    CallsPerFrame = frames > 0 ? (float)p.renders / frames : 0f,
                    UsPerCall = p.renders > 0 ? (float)(p.ticks * tickMs * 1000.0 / p.renders) : 0f
                });
                Destroy(p);
            }
            probes.Clear();
            mainProbe = null;
            byCamera?.Sort((a, b) => b.MsPerFrame.CompareTo(a.MsPerFrame));
            return main;
        }

        void OnPreCull() => start = Stopwatch.GetTimestamp();

        void OnPostRender()
        {
            if (start == 0) return;
            ticks += Stopwatch.GetTimestamp() - start;
            renders++;
            start = 0;
        }
    }
}
