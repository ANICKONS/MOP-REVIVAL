// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// render.shadows (requirement 9, design §8; ported from the shadow LOD of MOPR 5.x Next/RenderOptimizer.cs): a small
// object (diagonal up to 2.5 m) far enough away that its shadow would be a few pixels stops casting it.
//   * Distance: the shadow is dropped where the object covers less than 6 px on screen (screen height and the main
//     camera's field of view), never closer than 10 m.
//   * Only scene renderers with shadowCastingMode On (never prefabs, never MOPR6's own objects), never a renderer
//     whose visibility the game reads (RenderScan.IsObserved: Unity counts a drawn shadow as "visible").
//   * Through the shadow arbiter (ShadowCasting = Off, reason "render.shadows"): the ledger keeps the original and
//     gives it back when the object comes near, at the save barrier, with the main switch or when the system goes.
//     The game writing the mode itself wins (External): the entry is let go and taken again only if it is On.
// Scan: every active renderer at install and every 30 s (objects switched on later, bought items); the distance
// check runs over at most 1500 entries per tick (0.25 s).

using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;
using UnityEngine.Rendering;

namespace MOPR6.Render
{
    public sealed class RenderShadows : Optimization
    {
        public static readonly RenderShadows Instance = new RenderShadows();
        public const string SystemId = "render.shadows";

        const float MaxSize = 2.5f;
        const float ShadowPixels = 6f;
        const float MinDistance = 10f;
        const int PerTick = 1500;
        const double RescanSeconds = 30;

        sealed class Entry
        {
            public Renderer Renderer;
            public Transform Transform;
            public int Id;
            public float Size;
            public float SqrDistance;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly HashSet<int> known = new HashSet<int>();
        int cursor;
        double nextScan;
        float lastPpm;
        long hidden, shown, skippedObserved, gone;

        RenderShadows() : base(SystemId) { }

        public override string Title => "Shadow LOD";
        public override Tier Tier => Tier.Render;
        // Off by default: no measurable gain in vanilla (render-quick / merge-quick, 2026-10-29; Docs/render.ru.md).
        public override bool DefaultOn => false;

        // The scan is kept while the scene lives (switching the system off and on costs nothing); the held shadows
        // themselves go back through the arbiter when the system is removed.
        protected internal override string OnInstall(SystemMode mode)
        {
            cursor = 0;
            lastPpm = -1f;
            return null;
        }

        protected internal override void OnRemove() { }

        protected internal override void OnScene()
        {
            entries.Clear();
            known.Clear();
            nextScan = 0;
        }

        protected internal override void OnTick(double now)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (now >= nextScan)
            {
                nextScan = now + RescanSeconds;
                Scan();
            }
            float ppm = RenderScan.PixelsPerMeter(cam);
            if (!Mathf.Approximately(ppm, lastPpm))
            {
                lastPpm = ppm;
                foreach (Entry e in entries) e.SqrDistance = SqrDistanceFor(e.Size, ppm);
            }

            Vector3 camPos = cam.transform.position;
            ReasonArbiter a = Arbiters.Shadows;
            int n = Mathf.Min(PerTick, entries.Count);
            for (int i = 0; i < n && entries.Count > 0; i++)
            {
                if (cursor >= entries.Count) cursor = 0;
                Entry e = entries[cursor];
                if (e.Renderer == null)
                {
                    a.Remove(e.Id, Id);
                    entries.RemoveAt(cursor);
                    gone++;
                    continue;
                }
                cursor++;
                bool far = (e.Transform.position - camPos).sqrMagnitude > e.SqrDistance;
                bool held = a.Has(e.Id, Id);
                if (far && !held)
                {
                    // Only a shadow that is on now (the game may have switched it off or changed the mode).
                    if (e.Renderer.shadowCastingMode != ShadowCastingMode.On) continue;
                    if (a.Add(e.Id, Id) == SetResult.Ok) hidden++;
                }
                else if (!far && held)
                {
                    a.Remove(e.Id, Id);
                    shown++;
                }
            }
        }

        void Scan()
        {
            float ppm = RenderScan.PixelsPerMeter(Camera.main);
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (!(r is MeshRenderer)) continue;
                int id = r.GetInstanceID();
                if (!known.Add(id)) continue;
                if (r.shadowCastingMode != ShadowCastingMode.On || RenderScan.IsOurs(r.gameObject)) continue;
                // The Satsuma's parts belong to render.merge (a merged group has one shadow mode for all its parts).
                if (r.transform.root.name == SatsumaMerge.SatsumaPath) continue;
                if (RenderScan.IsObserved(r)) { skippedObserved++; continue; }
                float size = RenderScan.Size(r);
                if (size <= 0f || size > MaxSize) continue;
                entries.Add(new Entry { Renderer = r, Transform = r.transform, Id = Arbiters.Id(r), Size = size, SqrDistance = SqrDistanceFor(size, ppm) });
            }
        }

        static float SqrDistanceFor(float size, float ppm)
        {
            float d = Mathf.Max(MinDistance, size * ppm / ShadowPixels);
            return d * d;
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(entries.Count).Append(" small shadow casters, ").Append(Arbiters.Shadows != null ? Arbiters.Shadows.Held : 0)
              .Append(" without shadow now; switched off ").Append(hidden).Append(", back ").Append(shown).Append(", gone ").Append(gone)
              .Append(", left alone (visibility read by the game) ").Append(skippedObserved).Append('\n');
        }
    }
}
