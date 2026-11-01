// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Mesh merger (ported from MOPR 5.x Next/MeshMerger.cs, task 33): draws a vehicle as a handful of meshes merged by
// material instead of hundreds of separate parts. Visual only.
//
// Why: an assembled Satsuma has ~550 enabled renderers but only ~47 materials. Unity 5.0 issues one draw call per
// renderer, and the car is drawn even when it is behind a wall (the game has no occlusion culling). Measured in 5.x
// (TestBot, interleaved A/B): merging ~440 parts into 33 meshes saved 0.43-0.54 ms per frame net of the check.
//
// Exactness rules (unchanged from 5.x unless noted):
//   1. Only opaque MeshRenderers whose mesh, material and pose relative to the vehicle are stable are merged. Moving
//      parts, transparent or alpha-tested materials, multi-material parts, deformable panels, parts with a body of
//      their own and mirrored transforms stay as they are. Parts are only merged with parts drawn the same way:
//      material, shadow modes, layer, light probe use and - for shaders that sample them - the exact reflection
//      probes and weights Unity picks.
//   2. Every frame in LateUpdate, before any camera renders, every merged part is checked: its pose relative to the
//      vehicle (the part and every object up to the root, against the pose at merge time, 0.1 mm / ~0.01 degree),
//      deactivation, material swaps, and for 1/8 of the parts per frame Renderer.enabled and the shadow mode. Any
//      change gives the originals back before anything is drawn in that frame. Never from a camera callback: Unity
//      5.0 then loses the renderer.
//   3. 6.0: the merged meshes are NOT children of the vehicle (the game's scripts never see new children of the car,
//      requirement 9.1): they live under one MOPR_Merged_<name> object that takes the root's pose every LateUpdate.
//   4. 6.0: nothing is written directly. The originals are hidden through the render arbiter (reason
//      "render.merge"), the merged objects and meshes are CreatedObject entries of the ledger: the save barrier,
//      the main switch and "restore" give everything back even if the merger does not run any more.
//   5. Merging is time-sliced (2 ms per frame), so it never causes a hitch.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Ledger;
using MOPR6.Diagnostics;
using MOPR6.Ledger;
using UnityEngine;
using UnityEngine.Rendering;

namespace MOPR6.Render
{
    internal sealed class MeshMerger
    {
        const int MaxVerticesPerMesh = 64000;       // Unity 5.0: 16-bit indices
        const float BuildBudgetMs = 2f;             // time slice per frame while building
        const float ObserveSeconds = 1f;            // parts that move during this window are not merged
        const int GeometryQueue = 2000;
        const float DynamicBaseSeconds = 15f;       // a part that moved is skipped 15 s, then 30 s, then for good
        const int DynamicForeverAfter = 3;
        const float PosTolerance = 1e-4f;           // local units (metres): 0.1 mm
        const float RotTolerance = 1e-4f;           // quaternion components: ~0.01 degree
        const float ScaleTolerance = 1e-5f;

        /// <summary>A transform between a part and the root (or the part itself) with its local pose.</summary>
        sealed class Link
        {
            public Transform T;
            public Vector3 P, S;
            public Quaternion R;
            public bool Changed;

            public void Snapshot()
            {
                P = T.localPosition;
                R = T.localRotation;
                S = T.localScale;
            }

            /// <summary>Against the pose at merge time (slow drift adds up and is caught), far below a pixel.</summary>
            public bool Compare()
            {
                Vector3 p = T.localPosition;
                Quaternion r = T.localRotation;
                Vector3 s = T.localScale;
                Changed = Off(p.x, P.x, PosTolerance) || Off(p.y, P.y, PosTolerance) || Off(p.z, P.z, PosTolerance) ||
                          Off(r.x, R.x, RotTolerance) || Off(r.y, R.y, RotTolerance) || Off(r.z, R.z, RotTolerance) || Off(r.w, R.w, RotTolerance) ||
                          Off(s.x, S.x, ScaleTolerance) || Off(s.y, S.y, ScaleTolerance) || Off(s.z, S.z, ScaleTolerance);
                return Changed;
            }

            static bool Off(float a, float b, float tolerance)
            {
                float d = a - b;
                return d > tolerance || d < -tolerance;
            }

            public static bool Moved(Vector3 p, Vector3 p0, Quaternion q, Quaternion q0)
            {
                return Off(p.x, p0.x, PosTolerance) || Off(p.y, p0.y, PosTolerance) || Off(p.z, p0.z, PosTolerance) ||
                       Off(q.x, q0.x, RotTolerance) || Off(q.y, q0.y, RotTolerance) || Off(q.z, q0.z, RotTolerance) || Off(q.w, q0.w, RotTolerance);
            }
        }

        sealed class Part
        {
            public MeshRenderer Renderer;
            public int Id;                          // ledger id of the renderer
            public Transform Transform;
            public GameObject GameObject;           // cached: Component.gameObject is a native call
            public Material Material;               // first shared material (checked every frame)
            public ShadowCastingMode Shadows;
            public int[] Links;                     // own transform first, then the parents up to the root
            public ProbeBlend Blend;
        }

        sealed class Dynamic
        {
            public int Moves;
            public float Until;
        }

        sealed class Group
        {
            public Material Material;
            public ShadowCastingMode Shadows;
            public bool ReceiveShadows;
            public int Layer;
            public bool UseLightProbes;
            public ReflectionProbeUsage Reflections;
            public ProbeBlend Blend;
            public MeshRenderer Renderer;
            public readonly List<int> PartIndices = new List<int>();
            public readonly List<CombineInstance> Instances = new List<CombineInstance>();
            public int Vertices;
        }

        /// <summary>The reflection probes Unity picks for a renderer and their weights (compared exactly).</summary>
        sealed class ProbeBlend
        {
            public int[] Ids;
            public float[] Weights;
            public string Key;

            static readonly List<ReflectionProbeBlendInfo> buffer = new List<ReflectionProbeBlendInfo>(4);

            public static ProbeBlend Of(Renderer r)
            {
                buffer.Clear();
                r.GetClosestReflectionProbes(buffer);
                ProbeBlend b = new ProbeBlend { Ids = new int[buffer.Count], Weights = new float[buffer.Count] };
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < buffer.Count; i++)
                {
                    b.Ids[i] = buffer[i].probe != null ? buffer[i].probe.GetInstanceID() : 0;
                    b.Weights[i] = buffer[i].weight;
                    sb.Append(b.Ids[i]).Append(':').Append(BitConverter.DoubleToInt64Bits(b.Weights[i])).Append(';');
                }
                b.Key = sb.ToString();
                return b;
            }

            public bool Holds(Renderer r)
            {
                buffer.Clear();
                r.GetClosestReflectionProbes(buffer);
                if (buffer.Count != Ids.Length) return false;
                for (int i = 0; i < buffer.Count; i++)
                {
                    int id = buffer[i].probe != null ? buffer[i].probe.GetInstanceID() : 0;
                    if (id != Ids[i] || !buffer[i].weight.Equals(Weights[i])) return false;
                }
                return true;
            }
        }

        sealed class ProbeState
        {
            public ReflectionProbe Probe;
            public bool Enabled, Active;
            public Vector3 Position, Size, Center;
            public int Importance;

            public void Snapshot()
            {
                Enabled = Probe.enabled;
                Active = Probe.gameObject.activeInHierarchy;
                Position = Probe.transform.position;
                Size = Probe.size;
                Center = Probe.center;
                Importance = Probe.importance;
            }

            public bool Changed()
            {
                if (Probe == null) return true;
                if (Probe.enabled != Enabled || Probe.gameObject.activeInHierarchy != Active || Probe.importance != Importance) return true;
                Vector3 p = Probe.transform.position, s = Probe.size, c = Probe.center;
                return p.x != Position.x || p.y != Position.y || p.z != Position.z ||
                       s.x != Size.x || s.y != Size.y || s.z != Size.z ||
                       c.x != Center.x || c.y != Center.y || c.z != Center.z;
            }
        }

        enum State { Idle, Observing, Building, Merged }

        readonly Transform root;
        readonly string name;
        readonly string system;
        readonly Func<bool> eligible;
        readonly Dictionary<int, Dynamic> dynamicParts = new Dictionary<int, Dynamic>();
        readonly List<Part> parts = new List<Part>();
        readonly List<Link> links = new List<Link>();
        readonly List<MeshRenderer> mergedRenderers = new List<MeshRenderer>();
        readonly Dictionary<string, int> skipped = new Dictionary<string, int>();
        readonly List<string> dynamicNames = new List<string>();
        GameObject holder;
        int holderId;

        State state = State.Idle;
        float stateSince;
        string lastReason = "-";
        int merges, invalidations, rootMoves, sameValueWrites, vertices, lastBuildFrames, returnedByLedger;
        float lastBuildMs, lastBuildMaxSliceMs;
        bool flagsDirty;
        bool originalsShown;                         // A/B measurement only: merged state kept, originals drawn

        public bool IsMerged => state == State.Merged;
        public bool LastCheckFollowedBody { get; private set; }

        public MeshMerger(Transform root, string name, string system, Func<bool> eligible)
        {
            this.root = root;
            this.name = name;
            this.system = system;
            this.eligible = eligible;
        }

        /// <summary>Drives the state machine (system tick). Build runs as a coroutine on host.</summary>
        public void Tick(MonoBehaviour host)
        {
            if (root == null) return;
            float now = Time.realtimeSinceStartup;
            switch (state)
            {
                case State.Idle:
                    if (now - stateSince < 2f) return;      // let things settle after a change
                    if (!eligible()) { stateSince = now; return; }
                    BeginObserve(now);
                    break;
                case State.Observing:
                    if (!eligible()) { Abort("not eligible"); return; }
                    if (now - stateSince < ObserveSeconds) return;
                    host.StartCoroutine(Build());
                    break;
                case State.Merged:
                    if (!eligible()) Unmerge("not eligible");
                    break;
            }
        }

        void BeginObserve(float now)
        {
            parts.Clear();
            skipped.Clear();
            foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(false))
            {
                string why = SkipReason(r);
                if (why != null)
                {
                    int n;
                    skipped.TryGetValue(why, out n);
                    skipped[why] = n + 1;
                    continue;
                }
                parts.Add(new Part { Renderer = r, Transform = r.transform, GameObject = r.gameObject, Material = r.sharedMaterial, Shadows = r.shadowCastingMode });
            }
            BuildLinks();
            state = State.Observing;
            stateSince = now;
        }

        void BuildLinks()
        {
            links.Clear();
            Dictionary<Transform, int> index = new Dictionary<Transform, int>();
            List<int> chain = new List<int>(8);
            for (int i = 0; i < parts.Count; i++)
            {
                chain.Clear();
                for (Transform t = parts[i].Transform; t != null && t != root; t = t.parent)
                {
                    int k;
                    if (!index.TryGetValue(t, out k))
                    {
                        k = links.Count;
                        index[t] = k;
                        Link l = new Link { T = t };
                        l.Snapshot();
                        links.Add(l);
                    }
                    chain.Add(k);
                }
                parts[i].Links = chain.ToArray();
            }
        }

        void PruneLinks()
        {
            Dictionary<int, int> remap = new Dictionary<int, int>();
            List<Link> kept = new List<Link>();
            for (int i = 0; i < parts.Count; i++)
            {
                int[] l = parts[i].Links;
                for (int j = 0; j < l.Length; j++)
                {
                    int k;
                    if (!remap.TryGetValue(l[j], out k))
                    {
                        k = kept.Count;
                        kept.Add(links[l[j]]);
                        remap[l[j]] = k;
                    }
                    l[j] = k;
                }
            }
            links.Clear();
            links.AddRange(kept);
        }

        // ----- Reflection probes -------------------------------------------------------------------------------
        sealed class ProbeGroup
        {
            public ProbeBlend Blend;
            public MeshRenderer Renderer;
            public readonly List<Part> Parts = new List<Part>();
        }

        readonly List<ProbeGroup> probeGroups = new List<ProbeGroup>();
        readonly List<ProbeState> probeStates = new List<ProbeState>();
        Vector3 probeCheckPosition;
        Quaternion probeCheckRotation;

        bool ProbeGroupsHold()
        {
            for (int i = 0; i < probeGroups.Count; i++)
            {
                ProbeGroup g = probeGroups[i];
                if (g.Renderer == null || !g.Blend.Holds(g.Renderer)) return false;
                for (int k = 0; k < g.Parts.Count; k++)
                    if (g.Parts[k].Renderer == null || !g.Blend.Holds(g.Parts[k].Renderer)) return false;
            }
            return true;
        }

        void SnapshotProbes()
        {
            probeStates.Clear();
            if (probeGroups.Count == 0) return;
            foreach (ReflectionProbe p in Resources.FindObjectsOfTypeAll<ReflectionProbe>())
            {
                if (p == null) continue;
                ProbeState s = new ProbeState { Probe = p };
                s.Snapshot();
                probeStates.Add(s);
            }
            probeCheckPosition = root.position;
            probeCheckRotation = root.rotation;
        }

        string ProbesChanged()
        {
            if (probeGroups.Count == 0) return null;
            for (int i = 0; i < probeStates.Count; i++)
                if (probeStates[i].Changed()) return "reflection probe changed";
            Vector3 p = root.position;
            Quaternion q = root.rotation;
            if (Link.Moved(p, probeCheckPosition, q, probeCheckRotation))
            {
                if (!ProbeGroupsHold()) return "reflection probes differ";
                probeCheckPosition = p;
                probeCheckRotation = q;
            }
            return null;
        }

        bool CompareAllLinks()
        {
            bool any = false;
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].T == null) { links[i].Changed = true; any = true; continue; }
                if (links[i].Compare()) any = true;
            }
            return any;
        }

        bool PartMoved(Part p)
        {
            int[] l = p.Links;
            for (int i = 0; i < l.Length; i++)
                if (links[l[i]].Changed) return true;
            return false;
        }

        bool PartChainChanged(Part p)
        {
            int[] l = p.Links;
            bool any = false;
            for (int i = 0; i < l.Length; i++)
            {
                Link link = links[l[i]];
                if (link.T == null) { link.Changed = true; any = true; }
                else if (link.Compare()) any = true;
            }
            return any;
        }

        bool IsDynamic(int id, float now)
        {
            Dynamic d;
            if (!dynamicParts.TryGetValue(id, out d)) return false;
            return d.Moves >= DynamicForeverAfter || now < d.Until;
        }

        void MarkDynamic(Renderer r)
        {
            if (r == null) return;
            int id = r.GetInstanceID();
            Dynamic d;
            if (!dynamicParts.TryGetValue(id, out d))
            {
                dynamicParts[id] = d = new Dynamic();
                if (dynamicNames.Count < 40) dynamicNames.Add(r.transform.parent != null ? r.transform.parent.name + "/" + r.name : r.name);
            }
            d.Moves++;
            d.Until = Time.realtimeSinceStartup + DynamicBaseSeconds * d.Moves;
        }

        int MarkMovedParts()
        {
            int n = 0;
            for (int i = 0; i < parts.Count; i++)
                if (PartMoved(parts[i])) { MarkDynamic(parts[i].Renderer); n++; }
            return n;
        }

        int DynamicNowCount()
        {
            float now = Time.realtimeSinceStartup;
            int n = 0;
            foreach (KeyValuePair<int, Dynamic> kv in dynamicParts)
                if (kv.Value.Moves >= DynamicForeverAfter || now < kv.Value.Until) n++;
            return n;
        }

        string SkipReason(MeshRenderer r)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return "hidden";
            if (IsDynamic(r.GetInstanceID(), Time.realtimeSinceStartup)) return "moving";
            if (RenderScan.IsOurs(r.gameObject)) return "MOPR";
            // Something else holds this renderer in the ledger (another system hid it, or the game took it over).
            LedgerEntry held = Arbiters.Ledger != null ? Arbiters.Ledger.Find(new PropKey(r.GetInstanceID(), PropKind.RendererEnabled)) : null;
            if (held != null) return "held";
            if (RenderScan.IsObserved(r)) return "visibility read";

            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) return "no mesh";
            if (mesh.subMeshCount != 1) return "multi-material";
            Material mat = r.sharedMaterial;
            if (mat == null || mat.renderQueue != GeometryQueue) return "transparent";
            Material[] mats = r.sharedMaterials;
            if (mats == null || mats.Length != 1) return "multi-material";

            if (r.probeAnchor != null && (r.useLightProbes || SamplesReflectionProbes(mat))) return "probe anchor";
            if (r.useLightProbes && LightmapSettings.lightProbes != null && LightmapSettings.lightProbes.count > 0) return "light probes";

            // Panels that deform on impact rewrite their own mesh. Items lying in the car ("(itemx)") are picked up
            // and moved by the player (5.x: ItemBehaviour). Installed parts may keep a kinematic body of their own:
            // they are merged, the pose check sees them move (a body check excluded 348 of ~480 parts, measured).
            if (r.GetComponent<Deformable>() != null) return "deformable";
            for (Transform t = r.transform; t != null && t != root; t = t.parent)
                if (t.name.EndsWith("(itemx)", StringComparison.Ordinal)) return "item";
            if (Determinant3(r.transform.localToWorldMatrix) < 0f) return "mirrored";
            return null;
        }

        static bool SamplesReflectionProbes(Material m)
        {
            Shader s = m != null ? m.shader : null;
            return s == null || !s.name.StartsWith("Legacy Shaders/", StringComparison.Ordinal);
        }

        static float Determinant3(Matrix4x4 m)
        {
            return m.m00 * (m.m11 * m.m22 - m.m12 * m.m21)
                 - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20)
                 + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);
        }

        Matrix4x4 RelativePose(Part p)
        {
            Matrix4x4 m = Matrix4x4.identity;
            int[] l = p.Links;
            for (int i = l.Length - 1; i >= 0; i--)
            {
                Link link = links[l[i]];
                m = m * Matrix4x4.TRS(link.P, link.R, link.S);
            }
            return m;
        }

        IEnumerator Build()
        {
            state = State.Building;
            float buildStart = Time.realtimeSinceStartup, busyMs = 0f, maxSliceMs = 0f;
            int buildStartFrame = Time.frameCount;

            CompareAllLinks();
            List<Part> stable = new List<Part>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p.Renderer == null || !p.Renderer.enabled || !p.GameObject.activeInHierarchy) continue;
                if (PartMoved(p)) { MarkDynamic(p.Renderer); continue; }
                stable.Add(p);
            }
            parts.Clear();
            parts.AddRange(stable);
            BuildLinks();

            Dictionary<string, Group> groups = new Dictionary<string, Group>();
            List<Group> order = new List<Group>();
            for (int i = 0; i < parts.Count; i++)
            {
                MeshRenderer r = parts[i].Renderer;
                Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                Material mat = parts[i].Material;
                Matrix4x4 m = RelativePose(parts[i]);
                ReflectionProbeUsage refl = r.reflectionProbeUsage;
                string reflKey = "-";
                parts[i].Blend = null;
                parts[i].Shadows = r.shadowCastingMode;
                if (SamplesReflectionProbes(mat))
                {
                    reflKey = ((int)refl).ToString();
                    if (refl != ReflectionProbeUsage.Off)
                    {
                        parts[i].Blend = ProbeBlend.Of(r);
                        reflKey += "|" + parts[i].Blend.Key;
                    }
                }
                string key = mat.GetInstanceID() + "|" + (int)r.shadowCastingMode + "|" + r.receiveShadows + "|" +
                             r.gameObject.layer + "|" + r.useLightProbes + "|" + reflKey;
                int chunk = 0;
                Group g;
                while (groups.TryGetValue(key + "#" + chunk, out g) && g.Vertices + mesh.vertexCount > MaxVerticesPerMesh) chunk++;
                if (g == null)
                {
                    g = new Group
                    {
                        Material = mat, Shadows = r.shadowCastingMode, ReceiveShadows = r.receiveShadows,
                        Layer = r.gameObject.layer, UseLightProbes = r.useLightProbes, Reflections = refl,
                        Blend = parts[i].Blend
                    };
                    groups[key + "#" + chunk] = g;
                    order.Add(g);
                }
                g.Instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = 0, transform = m });
                g.PartIndices.Add(i);
                g.Vertices += mesh.vertexCount;
            }
            HashSet<int> notMerged = new HashSet<int>();
            List<Group> builtGroups = new List<Group>();

            // The holder takes the root's pose; nothing is shown until everything is ready.
            GameObject newHolder = new GameObject("MOPR_Merged_" + name);
            newHolder.transform.position = root.position;
            newHolder.transform.rotation = root.rotation;
            newHolder.transform.localScale = root.lossyScale;
            List<Mesh> meshes = new List<Mesh>();
            float sliceStart = buildStart;
            for (int i = 0; i < order.Count; i++)
            {
                GameObject go = null;
                Mesh combined = null;
                try
                {
                    Group g = order[i];
                    combined = new Mesh { name = "MOPR_Merged_" + name + "_" + i };
                    combined.CombineMeshes(g.Instances.ToArray(), true, true);
                    go = new GameObject("MOPR_Merged_" + name + "_" + i);
                    go.layer = g.Layer;
                    go.transform.SetParent(newHolder.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = combined;
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = g.Material;
                    mr.shadowCastingMode = g.Shadows;
                    mr.receiveShadows = g.ReceiveShadows;
                    mr.useLightProbes = g.UseLightProbes;
                    mr.reflectionProbeUsage = g.Reflections;
                    mr.enabled = false;
                    g.Renderer = mr;
                    if (g.Blend != null && !g.Blend.Holds(mr))
                    {
                        for (int k = 0; k < g.PartIndices.Count; k++) notMerged.Add(g.PartIndices[k]);
                        UnityEngine.Object.Destroy(combined);
                        UnityEngine.Object.Destroy(go);
                    }
                    else
                    {
                        meshes.Add(combined);
                        builtGroups.Add(g);
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Report(system + "/Build", ex, name);
                    if (combined != null) UnityEngine.Object.Destroy(combined);
                    DestroyBuilt(newHolder, meshes);
                    Abort("build error");
                    yield break;
                }

                float sliceMs = (Time.realtimeSinceStartup - sliceStart) * 1000f;
                if (sliceMs > BuildBudgetMs || i == order.Count - 1)
                {
                    busyMs += sliceMs;
                    if (sliceMs > maxSliceMs) maxSliceMs = sliceMs;
                }
                if (sliceMs > BuildBudgetMs)
                {
                    yield return null;
                    sliceStart = Time.realtimeSinceStartup;
                    if (state != State.Building || root == null) { DestroyBuilt(newHolder, meshes); yield break; }
                    newHolder.transform.position = root.position;
                    newHolder.transform.rotation = root.rotation;
                }
            }

            if (state != State.Building || root == null || !eligible())
            {
                DestroyBuilt(newHolder, meshes);
                Abort("changed while building");
                yield break;
            }

            probeGroups.Clear();
            for (int i = 0; i < builtGroups.Count; i++)
            {
                Group g = builtGroups[i];
                if (g.Blend == null) continue;
                ProbeGroup pg = new ProbeGroup { Blend = g.Blend, Renderer = g.Renderer };
                for (int k = 0; k < g.PartIndices.Count; k++) pg.Parts.Add(parts[g.PartIndices[k]]);
                probeGroups.Add(pg);
            }
            if (!ProbeGroupsHold())
            {
                DestroyBuilt(newHolder, meshes);
                probeGroups.Clear();
                Abort("reflection probes changed while building");
                yield break;
            }
            SnapshotProbes();
            if (notMerged.Count > 0)
            {
                List<Part> keep = new List<Part>(parts.Count - notMerged.Count);
                for (int i = 0; i < parts.Count; i++) if (!notMerged.Contains(i)) keep.Add(parts[i]);
                skipped["reflection probes"] = notMerged.Count;
                parts.Clear();
                parts.AddRange(keep);
                PruneLinks();
            }
            bool movedWhileBuilding = CompareAllLinks();
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p.Renderer == null || !p.Renderer.enabled || !p.GameObject.activeInHierarchy ||
                    p.Renderer.sharedMaterial != p.Material || (movedWhileBuilding && PartMoved(p)))
                {
                    if (movedWhileBuilding) MarkMovedParts();
                    DestroyBuilt(newHolder, meshes);
                    Abort("part changed while building");
                    yield break;
                }
            }
            lastBuildMs = busyMs;
            lastBuildMaxSliceMs = maxSliceMs;
            lastBuildFrames = Time.frameCount - buildStartFrame;

            for (int i = 0; i < parts.Count; i++) parts[i].Transform.hasChanged = false;
            root.hasChanged = false;
            flagsDirty = false;

            // Swap (same frame, before rendering): the merged objects into the ledger, then the originals hidden by
            // the render arbiter. A refused part (held by somebody else since) aborts the whole merge.
            holder = newHolder;
            holderId = Arbiters.Id(holder);
            if (Arbiters.Created.Add(holderId, system) != SetResult.Ok)
            {
                DestroyBuilt(newHolder, meshes);
                holder = null;
                Abort("ledger refused the merged object");
                yield break;
            }
            foreach (Mesh mesh in meshes) Arbiters.Created.Add(Arbiters.Id(mesh), system);
            mergedRenderers.Clear();
            vertices = 0;
            foreach (MeshRenderer mr in holder.GetComponentsInChildren<MeshRenderer>(true))
            {
                mergedRenderers.Add(mr);
                mr.enabled = true;
                vertices += mr.GetComponent<MeshFilter>().sharedMesh.vertexCount;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Id = Arbiters.Id(parts[i].Renderer);
                if (Arbiters.Render.Add(parts[i].Id, system) != SetResult.Ok)
                {
                    state = State.Merged;
                    Unmerge("ledger refused a part");
                    yield break;
                }
            }
            originalsShown = false;
            state = State.Merged;
            stateSince = Time.realtimeSinceStartup;
            merges++;
        }

        /// <summary>
        /// Once per frame from LateUpdate, before any camera renders: the holder takes the root's pose, and the
        /// originals come back if anything about a merged part changed, so the change is drawn in this very frame.
        /// </summary>
        public void Validate()
        {
            if (state != State.Merged) return;
            if (root == null) { Release(); return; }
            if (holder == null || !Arbiters.Created.Has(holderId, system))
            {
                // The ledger gave everything back (save barrier, main switch, restore): the originals are on.
                returnedByLedger++;
                Forget("returned by the ledger");
                return;
            }
            Transform h = holder.transform;
            h.position = root.position;
            h.rotation = root.rotation;

            bool rootMoved = root.hasChanged;
            bool exact = rootMoved || flagsDirty;
            LastCheckFollowedBody = exact;
            if (exact)
            {
                if (CompareAllLinks())
                {
                    MarkMovedParts();
                    Unmerge("part moved");
                    return;
                }
                if (rootMoved)
                {
                    root.hasChanged = false;
                    flagsDirty = true;
                    rootMoves++;
                }
                else
                {
                    for (int i = 0; i < parts.Count; i++) parts[i].Transform.hasChanged = false;
                    flagsDirty = false;
                }
            }

            string probes = ProbesChanged();
            if (probes != null) { Unmerge(probes); return; }

            int phase = Time.frameCount & 7;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                MeshRenderer r = p.Renderer;
                if (r == null) { Unmerge("part destroyed"); return; }
                if (!exact && p.Transform.hasChanged)
                {
                    // Written, but maybe with the same value (some state machines set positions every frame).
                    if (PartChainChanged(p)) { MarkDynamic(r); Unmerge("part moved"); return; }
                    p.Transform.hasChanged = false;
                    sameValueWrites++;
                }
                if (!p.GameObject.activeInHierarchy) { Unmerge("part hidden"); return; }
                if (r.sharedMaterial != p.Material) { Unmerge("material changed"); return; }
                if ((i & 7) == phase)
                {
                    if (r.enabled != originalsShown) { Unmerge("renderer enabled elsewhere"); return; }
                    if (r.shadowCastingMode != p.Shadows) { Unmerge("shadow mode changed"); return; }
                }
            }
        }

        /// <summary>A/B measurement only (TestBot): keep the merge, draw the originals instead (through the arbiter).</summary>
        public void SetOriginalsShown(bool shown)
        {
            if (state != State.Merged || shown == originalsShown) return;
            originalsShown = shown;
            for (int i = 0; i < mergedRenderers.Count; i++)
                if (mergedRenderers[i] != null) mergedRenderers[i].enabled = !shown;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Renderer == null) continue;
                if (shown) Arbiters.Render.Remove(parts[i].Id, system);
                else Arbiters.Render.Add(parts[i].Id, system);
            }
        }

        /// <summary>The originals back (through the arbiter) and the merged objects gone (through the ledger).</summary>
        public void Unmerge(string reason)
        {
            lastReason = reason;
            if (state == State.Merged)
            {
                invalidations++;
                for (int i = 0; i < parts.Count; i++) Arbiters.Render.Remove(parts[i].Id, system);
                if (holder != null)
                {
                    foreach (MeshFilter mf in holder.GetComponentsInChildren<MeshFilter>(true))
                        if (mf.sharedMesh != null) Arbiters.Created.Remove(mf.sharedMesh.GetInstanceID(), system);
                    Arbiters.Created.Remove(holderId, system);
                }
            }
            Forget(reason);
        }

        /// <summary>Back to idle without touching anything (whatever was held is back already).</summary>
        void Forget(string reason)
        {
            lastReason = reason;
            holder = null;
            mergedRenderers.Clear();
            originalsShown = false;
            parts.Clear();
            links.Clear();
            probeGroups.Clear();
            probeStates.Clear();
            state = State.Idle;
            stateSince = Time.realtimeSinceStartup;
        }

        void Abort(string reason) => Forget(reason);

        public void Release()
        {
            if (state == State.Merged) Unmerge("released");
            else Abort("released");
        }

        static void DestroyBuilt(GameObject holderObject, List<Mesh> meshes)
        {
            foreach (Mesh m in meshes) if (m != null) UnityEngine.Object.Destroy(m);
            meshes.Clear();
            if (holderObject != null) UnityEngine.Object.Destroy(holderObject);
        }

        public string Report()
        {
            return name + ": " + state + ", " + parts.Count + " parts (" + links.Count + " transforms checked) in " + mergedRenderers.Count +
                   " meshes (" + vertices + " vertices), " + DynamicNowCount() + " moving parts skipped, " + merges + " merges, " +
                   invalidations + " invalidations, " + returnedByLedger + " returned by the ledger, body moves followed: " + rootMoves +
                   ", same-value writes: " + sameValueWrites + ", last build " + lastBuildMs.ToString("0.0") + " ms over " + lastBuildFrames +
                   " frames (max " + lastBuildMaxSliceMs.ToString("0.0") + " ms/frame), last reason: " + lastReason +
                   (originalsShown ? ", A/B: originals shown" : "") + ", not merged: " + SkippedText() +
                   (dynamicNames.Count > 0 ? ", moving: " + string.Join(", ", dynamicNames.ToArray()) : "");
        }

        string SkippedText()
        {
            if (skipped.Count == 0) return "-";
            List<string> s = new List<string>();
            foreach (KeyValuePair<string, int> kv in skipped) s.Add(kv.Key + " " + kv.Value);
            return string.Join(", ", s.ToArray());
        }
    }
}
