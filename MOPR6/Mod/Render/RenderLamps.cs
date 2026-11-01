// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// render.lamps (requirement 9, design §8; ported from the lamp shadows of MOPR 5.x Next/RenderOptimizer.cs): a point or
// spot light with real-time shadows stops casting them while the camera is farther than its range plus 35 m - its
// light does not reach the camera's surroundings, and every shadowed light costs a shadow map render per frame.
// The light itself stays on (LightEnabled is never touched), only Light.shadows goes to None through the lamp shadow
// arbiter; the ledger gives the original back when the camera comes near, at the save barrier, with the main switch.
// The game writing the shadows itself wins (External): the light is let go and taken again only while it has shadows.
// Scan: every active light at install and every 30 s; the check runs over all of them once per second.

using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;

namespace MOPR6.Render
{
    public sealed class RenderLamps : Optimization
    {
        public static readonly RenderLamps Instance = new RenderLamps();
        public const string SystemId = "render.lamps";

        const float Margin = 35f;
        const double CheckSeconds = 1, RescanSeconds = 30;

        sealed class Entry
        {
            public Light Light;
            public Transform Transform;
            public int Id;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly HashSet<int> known = new HashSet<int>();
        double nextCheck, nextScan;
        long off, on, gone;

        RenderLamps() : base(SystemId) { }

        public override string Title => "Lamp shadows";
        public override Tier Tier => Tier.Render;
        // Off by default: no measurable gain in vanilla (render-quick / merge-quick, 2026-10-29; Docs/render.ru.md).
        public override bool DefaultOn => false;

        // The scan is kept while the scene lives; the held shadows go back through the arbiter when the system goes.
        protected internal override string OnInstall(SystemMode mode)
        {
            nextCheck = 0;
            return null;
        }

        protected internal override void OnRemove() { }

        protected internal override void OnScene()
        {
            entries.Clear();
            known.Clear();
            nextCheck = nextScan = 0;
        }

        protected internal override void OnTick(double now)
        {
            if (now < nextCheck) return;
            nextCheck = now + CheckSeconds;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (now >= nextScan)
            {
                nextScan = now + RescanSeconds;
                foreach (Light l in Object.FindObjectsOfType<Light>())
                {
                    if (l.type == LightType.Directional || !known.Add(l.GetInstanceID()) || RenderScan.IsOurs(l.gameObject)) continue;
                    entries.Add(new Entry { Light = l, Transform = l.transform, Id = Arbiters.Id(l) });
                }
            }

            Vector3 camPos = cam.transform.position;
            ReasonArbiter a = Arbiters.LampShadows;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                if (e.Light == null)
                {
                    a.Remove(e.Id, Id);
                    entries.RemoveAt(i);
                    gone++;
                    continue;
                }
                float d = e.Light.range + Margin;
                bool far = (e.Transform.position - camPos).sqrMagnitude > d * d;
                bool held = a.Has(e.Id, Id);
                if (far && !held)
                {
                    if (e.Light.shadows == LightShadows.None) continue;
                    if (a.Add(e.Id, Id) == SetResult.Ok) off++;
                }
                else if (!far && held)
                {
                    a.Remove(e.Id, Id);
                    on++;
                }
            }
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(entries.Count).Append(" lights, ").Append(Arbiters.LampShadows != null ? Arbiters.LampShadows.Held : 0)
              .Append(" without shadows now; switched off ").Append(off).Append(", back ").Append(on).Append(", gone ").Append(gone).Append('\n');
        }
    }
}
