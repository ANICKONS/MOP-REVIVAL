// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The stand-in of a vehicle switched off by world.deactivate (design §9.2): a still copy of what the vehicle shows at
// that moment, so the picture does not change (MOPR 5.x drew a stripped clone, LodObject). Nothing of the vehicle is
// cloned: new objects with the vehicle's meshes and materials only, no colliders, no scripts.
//   * Opaque or not, a MeshRenderer whose submeshes match its materials, not mirrored, not lightmapped, is merged with
//     the others of the same material, shadow modes, layer and probe use (one draw call per group, 64k vertices each).
//   * Everything else drawn is copied one by one at its world pose: mirrored parts, lightmapped ones, skinned meshes
//     (baked as they are now), projectors (blob shadows).
//   * Line, trail and particle renderers are left out (counted): a playing particle system keeps the vehicle on.
// The holder is a root object MOPR_StandIn_<name> (the game never sees new children of its objects; the harness
// leaves MOPR_* out), built inactive and shown in the same frame the vehicle goes off. The holder and every mesh made
// here are CreatedObject entries of the ledger: the save barrier, the main switch and "restore" destroy them after
// the vehicle is on again.

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Ledger;
using MOPR6.Ledger;
using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MOPR6.World
{
    internal sealed class StandIn
    {
        const int MaxVertices = 64000;

        sealed class Group
        {
            public Material Material;
            public ShadowCastingMode Shadows;
            public bool Receive, Probes;
            public int Layer;
            public ReflectionProbeUsage Reflections;
            public readonly List<CombineInstance> Parts = new List<CombineInstance>();
            public int Vertices;
        }

        public GameObject Holder;
        int holderId;
        readonly List<int> meshIds = new List<int>();
        readonly string system;
        public int Merged, Groups, Copies, Projectors;
        public double BuildMs;
        public readonly Dictionary<string, int> Left = new Dictionary<string, int>(StringComparer.Ordinal);

        StandIn(string system) { this.system = system; }

        /// <summary>Builds the stand-in of <paramref name="root"/> (inactive, registered in the ledger). Null and the reason on failure.</summary>
        public static StandIn Build(Transform root, List<Renderer> renderers, List<Projector> projectors, string system, out string error)
        {
            long t0 = Stopwatch.GetTimestamp();
            StandIn s = new StandIn(system);
            error = null;
            GameObject holder = new GameObject("MOPR_StandIn_" + root.name);
            holder.SetActive(false);
            Transform h = holder.transform;
            h.position = root.position;
            h.rotation = root.rotation;
            Matrix4x4 toLocal = h.worldToLocalMatrix;
            List<Mesh> made = new List<Mesh>();
            try
            {
                Dictionary<string, Group> groups = new Dictionary<string, Group>(StringComparer.Ordinal);
                List<Group> order = new List<Group>();
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    MeshRenderer mr = r as MeshRenderer;
                    SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
                    Mesh mesh = null;
                    bool baked = false;
                    if (mr != null)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    else if (sk != null && sk.sharedMesh != null)
                    {
                        mesh = new Mesh { name = "MOPR_StandIn_baked" };
                        sk.BakeMesh(mesh);
                        made.Add(mesh);
                        baked = true;
                    }
                    else { s.Count(r.GetType().Name); continue; }
                    if (mesh == null || mesh.vertexCount == 0) { s.Count("no mesh"); continue; }
                    Material[] mats = r.sharedMaterials;
                    bool lightmapped = r.lightmapIndex >= 0 && r.lightmapIndex < 254;
                    bool mirrored = Determinant3(r.transform.localToWorldMatrix) < 0f;
                    bool simple = mr != null && !lightmapped && !mirrored && mats != null && mats.Length == mesh.subMeshCount && AllSet(mats) &&
                                  mesh.vertexCount <= MaxVertices;
                    if (!simple) { s.Copy(h, r, mesh, mats, baked); continue; }
                    Matrix4x4 m = toLocal * r.transform.localToWorldMatrix;
                    for (int sub = 0; sub < mats.Length; sub++)
                    {
                        string key = mats[sub].GetInstanceID() + "|" + (int)r.shadowCastingMode + "|" + r.receiveShadows + "|" + r.gameObject.layer + "|" +
                                     r.useLightProbes + "|" + (int)r.reflectionProbeUsage;
                        int chunk = 0;
                        Group g;
                        while (groups.TryGetValue(key + "#" + chunk, out g) && g.Vertices + mesh.vertexCount > MaxVertices) chunk++;
                        if (g == null)
                        {
                            g = new Group
                            {
                                Material = mats[sub], Shadows = r.shadowCastingMode, Receive = r.receiveShadows, Layer = r.gameObject.layer,
                                Probes = r.useLightProbes, Reflections = r.reflectionProbeUsage
                            };
                            groups[key + "#" + chunk] = g;
                            order.Add(g);
                        }
                        g.Parts.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = m });
                        g.Vertices += mesh.vertexCount;
                    }
                    s.Merged++;
                }
                for (int i = 0; i < order.Count; i++)
                {
                    Group g = order[i];
                    Mesh combined = new Mesh { name = "MOPR_StandIn_" + root.name + "_" + i };
                    made.Add(combined);
                    combined.CombineMeshes(g.Parts.ToArray(), true, true);
                    GameObject go = new GameObject("MOPR_StandIn_" + i);
                    go.layer = g.Layer;
                    go.transform.SetParent(h, false);
                    go.AddComponent<MeshFilter>().sharedMesh = combined;
                    MeshRenderer r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = g.Material;
                    r.shadowCastingMode = g.Shadows;
                    r.receiveShadows = g.Receive;
                    r.useLightProbes = g.Probes;
                    r.reflectionProbeUsage = g.Reflections;
                }
                s.Groups = order.Count;
                for (int i = 0; i < projectors.Count; i++)
                {
                    Projector p = projectors[i];
                    if (p == null || !p.enabled || !p.gameObject.activeInHierarchy) continue;
                    GameObject go = new GameObject("MOPR_StandIn_projector");
                    go.layer = p.gameObject.layer;
                    go.transform.SetParent(h, false);
                    go.transform.position = p.transform.position;
                    go.transform.rotation = p.transform.rotation;
                    Projector q = go.AddComponent<Projector>();
                    q.nearClipPlane = p.nearClipPlane;
                    q.farClipPlane = p.farClipPlane;
                    q.fieldOfView = p.fieldOfView;
                    q.aspectRatio = p.aspectRatio;
                    q.orthographic = p.orthographic;
                    q.orthographicSize = p.orthographicSize;
                    q.ignoreLayers = p.ignoreLayers;
                    q.material = p.material;
                    s.Projectors++;
                }
            }
            catch (Exception ex)
            {
                MOPR6.Diagnostics.ModLog.Report(system + "/StandIn", ex, root.name);
                error = ex.GetType().Name + ": " + ex.Message;
                foreach (Mesh m in made) if (m != null) UnityEngine.Object.Destroy(m);
                UnityEngine.Object.Destroy(holder);
                return null;
            }
            // Into the ledger: from here on the save barrier, the main switch and "restore" destroy it.
            s.Holder = holder;
            s.holderId = Arbiters.Id(holder);
            if (Arbiters.Created.Add(s.holderId, system) != SetResult.Ok)
            {
                error = "the ledger refused the stand-in";
                foreach (Mesh m in made) if (m != null) UnityEngine.Object.Destroy(m);
                UnityEngine.Object.Destroy(holder);
                return null;
            }
            foreach (Mesh m in made)
            {
                int id = Arbiters.Id(m);
                if (Arbiters.Created.Add(id, system) == SetResult.Ok) s.meshIds.Add(id);
                else UnityEngine.Object.Destroy(m);
            }
            s.BuildMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return s;
        }

        void Count(string what) => Left[what] = (Left.TryGetValue(what, out int n) ? n : 0) + 1;

        void Copy(Transform holder, Renderer r, Mesh mesh, Material[] mats, bool baked)
        {
            GameObject go = new GameObject("MOPR_StandIn_part");
            go.layer = r.gameObject.layer;
            Transform t = go.transform;
            t.SetParent(holder, false);
            t.position = r.transform.position;
            t.rotation = r.transform.rotation;
            // A baked skinned mesh is in the renderer's space with its scale applied already.
            Vector3 world = baked ? Vector3.one : r.transform.lossyScale;
            t.localScale = world;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer c = go.AddComponent<MeshRenderer>();
            c.sharedMaterials = mats;
            c.shadowCastingMode = r.shadowCastingMode;
            c.receiveShadows = r.receiveShadows;
            c.useLightProbes = r.useLightProbes;
            c.reflectionProbeUsage = r.reflectionProbeUsage;
            if (r.lightmapIndex >= 0 && r.lightmapIndex < 254)
            {
                c.lightmapIndex = r.lightmapIndex;
                c.lightmapScaleOffset = r.lightmapScaleOffset;
            }
            Copies++;
        }

        static bool AllSet(Material[] mats)
        {
            for (int i = 0; i < mats.Length; i++) if (mats[i] == null) return false;
            return true;
        }

        static float Determinant3(Matrix4x4 m)
        {
            return m.m00 * (m.m11 * m.m22 - m.m12 * m.m21)
                 - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20)
                 + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);
        }

        public void Show(bool shown)
        {
            if (Holder != null && Holder.activeSelf != shown) Holder.SetActive(shown);
        }

        /// <summary>Gone through the ledger (it destroys the holder and the meshes made here).</summary>
        public void Release()
        {
            for (int i = 0; i < meshIds.Count; i++) Arbiters.Created.Remove(meshIds[i], system);
            meshIds.Clear();
            Arbiters.Created.Remove(holderId, system);
            Holder = null;
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Merged).Append(" parts in ").Append(Groups).Append(" meshes, ").Append(Copies).Append(" copies, ").Append(Projectors).Append(" projectors, ")
              .Append(BuildMs.ToString(BuildMs < 10 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            if (Left.Count > 0)
            {
                sb.Append(", left out:");
                foreach (KeyValuePair<string, int> kv in Left) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
