// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Satsuma roof lamp fix (5.1, a vanilla model bug). The interior lamp's housing is part of the "cowl_parts"
// mesh, and its upper rim sticks out of the roof by up to ~7 mm (measured with the TestBot: meshclip, merger
// off, so it is the game's own model). From outside it shows as a dark arc on the roof, with the lamp switch
// next to it.
//
// Fix, once at load, visual only: in the car's own frame, every vertex of the lamp housing that is above the
// roof's outer surface (minus a 1.5 mm margin) is moved straight down to that height, in a copy of the mesh
// (the original asset and the collider are untouched). The lamp itself (switch and lens, in both switch
// positions) is lowered just as far as needed to stay under the roof. Nothing else changes: no collider, no
// physics, no PlayMaker, nothing that is saved.

using System;
using System.Collections.Generic;
using MSCLoader;
using UnityEngine;

using MOP.Common;

namespace MOP.Next
{
    internal static class RoofLampFix
    {
        const float Margin = 0.0015f;       // the lamp ends up at least 1.5 mm under the outer roof surface
        const float HousingRadius = 0.06f;  // the housing is ~9 cm across; nothing else of cowl_parts is that close
        const float HeightWindow = 0.04f;   // only geometry within 4 cm above/below the lamp centre
        const float PivotAngle = 12f;       // the lamp switch is turned between +12 and -12 degrees (FSM "Use")

        /// <summary>Applies the fix to the Satsuma (no-op if anything looks different from the stock car).</summary>
        internal static void Apply(Transform car)
        {
            if (car == null) return;
            Transform body = car.Find("Body/car body(xxxxx)");
            Transform cowl = car.Find("Body/cowl_parts");
            Transform lamp = car.Find("Interior/InteriorLight");
            MeshFilter bodyMf = body != null ? body.GetComponent<MeshFilter>() : null;
            MeshFilter cowlMf = cowl != null ? cowl.GetComponent<MeshFilter>() : null;
            if (bodyMf == null || cowlMf == null || lamp == null || bodyMf.sharedMesh == null || cowlMf.sharedMesh == null)
            {
                ModConsole.Log("[MOPR] Roof lamp fix: not a stock Satsuma roof, skipped.");
                return;
            }

            // The lamp's geometry (switch in both positions, and the lens) in the car's frame. Its top, not the
            // lamp object's origin (which sits a few centimetres lower), is where the roof is looked for.
            List<Vector3> lampPoints = LampPoints(car, lamp);
            if (lampPoints.Count == 0)
            {
                ModConsole.Log("[MOPR] Roof lamp fix: the lamp has no mesh, skipped.");
                return;
            }
            Vector3 centre = car.InverseTransformPoint(lamp.position);
            float top = float.MinValue;
            for (int i = 0; i < lampPoints.Count; i++) top = Mathf.Max(top, lampPoints[i].y);
            centre.y = top;

            string why;
            List<Vector3> roof = RoofTriangles(car, bodyMf, centre, out why);
            if (roof.Count == 0)
            {
                ModConsole.Log($"[MOPR] Roof lamp fix: no roof found above the lamp ({why}), skipped.");
                return;
            }

            // 1. The housing (part of cowl_parts): clamp the vertices that stick out.
            Mesh source = cowlMf.sharedMesh;
            Vector3[] v = source.vertices;
            Matrix4x4 cowlToCar = car.worldToLocalMatrix * cowl.localToWorldMatrix;
            Matrix4x4 carToCowl = cowlToCar.inverse;
            int moved = 0;
            float stuckOut = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = cowlToCar.MultiplyPoint3x4(v[i]);
                if (!NearLamp(p, centre)) continue;
                float roofY;
                if (!RoofHeight(roof, p.x, p.z, centre.y, out roofY)) continue;
                float limit = roofY - Margin;
                if (p.y <= limit) continue;
                stuckOut = Mathf.Max(stuckOut, p.y - roofY);
                p.y = limit;
                v[i] = carToCowl.MultiplyPoint3x4(p);
                moved++;
            }
            if (moved > 0)
            {
                Mesh copy = UnityEngine.Object.Instantiate(source);
                copy.name = source.name + " (MOPR roof lamp fix)";
                copy.vertices = v;
                copy.RecalculateBounds();
                cowlMf.sharedMesh = copy;
                fixedMesh = copy;
            }
            housing = cowlMf;
            originalMesh = source;
            lampTransform = lamp;
            originalLampPosition = lamp.localPosition;

            // 2. The lamp: lower it as far as its highest point needs (switch in both positions).
            float lower = 0f;
            for (int i = 0; i < lampPoints.Count; i++)
            {
                Vector3 p = lampPoints[i];
                float roofY;
                if (!RoofHeight(roof, p.x, p.z, centre.y, out roofY)) continue;
                lower = Mathf.Max(lower, p.y - (roofY - Margin));
            }
            if (lower > 0f) lamp.position -= car.up * lower;
            fixedLampPosition = lamp.localPosition;

            ModConsole.Log($"[MOPR] Roof lamp fix: {moved} vertices of the lamp housing lowered (it stuck out of the roof " +
                           $"by up to {stuckOut * 1000f:0.0} mm), lamp lowered by {lower * 1000f:0.0} mm.");
        }

        static MeshFilter housing;
        static Mesh originalMesh, fixedMesh;
        static Transform lampTransform;
        static Vector3 originalLampPosition, fixedLampPosition;

        /// <summary>
        /// Before/after comparison (TestBot, through reflection): true puts the stock mesh and lamp position back,
        /// false applies the fix again. Not used by MOP itself.
        /// </summary>
        internal static bool ShowOriginal
        {
            get { return housing != null && housing.sharedMesh == originalMesh; }
            set
            {
                if (housing == null || lampTransform == null) return;
                if (fixedMesh != null) housing.sharedMesh = value ? originalMesh : fixedMesh;
                lampTransform.localPosition = value ? originalLampPosition : fixedLampPosition;
            }
        }

        /// <summary>Every vertex of the lamp's meshes in the car's frame, with the switch in both positions.</summary>
        static List<Vector3> LampPoints(Transform car, Transform lamp)
        {
            List<Vector3> points = new List<Vector3>();
            Transform pivot = lamp.Find("Pivot");
            Quaternion pivotRotation = pivot != null ? pivot.localRotation : Quaternion.identity;
            try
            {
                for (int pass = 0; pass < (pivot != null ? 2 : 1); pass++)
                {
                    if (pivot != null) pivot.localRotation = Quaternion.Euler(0f, pass == 0 ? PivotAngle : -PivotAngle, 0f);
                    foreach (MeshFilter mf in lamp.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf == null || mf.sharedMesh == null) continue;
                        Matrix4x4 toCar = car.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                        Vector3[] lv = mf.sharedMesh.vertices;
                        for (int i = 0; i < lv.Length; i++) points.Add(toCar.MultiplyPoint3x4(lv[i]));
                    }
                }
            }
            finally
            {
                if (pivot != null) pivot.localRotation = pivotRotation;   // the same frame: nothing sees the turn
            }
            return points;
        }

        static bool NearLamp(Vector3 p, Vector3 centre)
        {
            float dx = p.x - centre.x, dz = p.z - centre.z;
            return dx * dx + dz * dz <= HousingRadius * HousingRadius && Mathf.Abs(p.y - centre.y) <= HeightWindow;
        }

        /// <summary>The roof's triangles above the lamp that face up (the outer surface), in the car's frame.</summary>
        static List<Vector3> RoofTriangles(Transform car, MeshFilter bodyMf, Vector3 centre, out string stats)
        {
            List<Vector3> list = new List<Vector3>();
            Mesh mesh = bodyMf.sharedMesh;
            Vector3[] v = mesh.vertices;
            int[] t = mesh.triangles;
            Matrix4x4 toCar = car.worldToLocalMatrix * bodyMf.transform.localToWorldMatrix;
            float reach = HousingRadius + 0.2f;
            int near = 0, inWindow = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = toCar.MultiplyPoint3x4(v[t[i]]), b = toCar.MultiplyPoint3x4(v[t[i + 1]]), c = toCar.MultiplyPoint3x4(v[t[i + 2]]);
                if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < centre.x - reach || Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > centre.x + reach) continue;
                if (Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < centre.z - reach || Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > centre.z + reach) continue;
                near++;
                if (Mathf.Max(a.y, Mathf.Max(b.y, c.y)) < centre.y - HeightWindow || Mathf.Min(a.y, Mathf.Min(b.y, c.y)) > centre.y + HeightWindow) continue;
                inWindow++;
                if (Vector3.Cross(b - a, c - a).y <= 0f) continue;   // faces down: not seen from above
                list.Add(a); list.Add(b); list.Add(c);
            }
            stats = $"{t.Length / 3} body triangles, {near} around the lamp, {inWindow} at its height, {list.Count / 3} facing up; " +
                    $"lamp at {centre.ToString("F3")} in the car, body at {car.InverseTransformPoint(bodyMf.transform.position).ToString("F3")}";
            return list;
        }

        /// <summary>Height of the highest up-facing roof triangle straight above or below (x, z), near the lamp.</summary>
        static bool RoofHeight(List<Vector3> tris, float x, float z, float centreY, out float y)
        {
            y = float.MinValue;
            bool found = false;
            for (int k = 0; k + 2 < tris.Count; k += 3)
            {
                Vector3 a = tris[k], b = tris[k + 1], c = tris[k + 2];
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-12f) continue;
                float l1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                float l2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                float l3 = 1f - l1 - l2;
                if (l1 < 0f || l2 < 0f || l3 < 0f) continue;
                float h = l1 * a.y + l2 * b.y + l3 * c.y;
                if (Mathf.Abs(h - centreY) > HeightWindow) continue;
                if (h > y) { y = h; found = true; }
            }
            return found;
        }
    }
}
