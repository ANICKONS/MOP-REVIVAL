// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Mesh merger (5.1): draws a vehicle as a handful of meshes merged by material instead of hundreds of
// separate parts. Visual only.
//
// Why: an assembled Satsuma has ~550 enabled renderers but only ~47 materials. Unity 5.0 issues one draw
// call per renderer, the car is drawn even when it is behind a wall (the game has no occlusion culling),
// and while driving it is drawn three times (main camera and two mirror cameras). Measured near the car
// (TestBot, interleaved A/B): the car costs 0.86 ms per frame to draw; merging ~440 of its parts into
// 33 meshes saves 0.5-0.6 ms per frame net of the per-frame check.
//
// Exactness rules:
//   1. Only opaque MeshRenderers whose mesh, material and pose relative to the vehicle are stable are merged.
//      Parts that move (needles, wipers, wheels, suspension...), transparent or alpha-tested materials
//      (sorting), deformable panels (they rewrite their own mesh), items and mirrored transforms stay as
//      they are. Parts are only merged with parts that are drawn the same way: material, shadow modes, layer,
//      light probe use and - for shaders that sample them - reflection probe use and the exact reflection
//      probes and weights Unity picks (see "Reflection probes" below).
//   2. Every frame in LateUpdate, before any camera renders, every merged part is checked: its pose relative
//      to the vehicle (local position, rotation and scale of the part and of every object between it and the
//      vehicle root, compared with the pose at merge time within 0.1 mm / ~0.01 degree - far below a pixel),
//      deactivation and material swaps. Any change restores the original
//      renderers before anything is drawn in that frame; a part that moved is then skipped for a while.
//      Renderer.enabled is never changed from a camera callback: Unity 5.0 then loses the renderer (it reads
//      enabled but is never drawn again). See VehicleMeshMerge.LateUpdate.
//      (A renderer switched on by someone else is checked for 1/8 of the parts per frame: the same surface is
//      then drawn twice, with the same material, for at most 8 frames.)
//      The merged meshes are children of the vehicle root, so they follow the body exactly when it moves.
//   3. It never moves, enables or disables a game object, never touches colliders, physics or PlayMaker.
//      The only thing it changes is Renderer.enabled of the merged originals, and it restores it on any
//      change, before saving, when the setting is turned off and when MOP stops.
//   4. Merging is time-sliced (a few ms per frame), so it never causes a hitch.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

using MOP.Common;

namespace MOP.Next
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

            /// <summary>
            /// Compared with the pose at merge time (not with the previous frame, so slow drift adds up and is
            /// caught). The tolerance is far below a pixel: 0.1 mm, ~0.01 degree. Without it, the wheel and
            /// suspension parts of a car with physics on count as moving: the wheel script rewrites their
            /// positions every physics step with differences of a few micrometres.
            /// </summary>
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

            /// <summary>World pose moved by more than the pose tolerance.</summary>
            public static bool Moved(Vector3 p, Vector3 p0, Quaternion q, Quaternion q0)
            {
                return Off(p.x, p0.x, PosTolerance) || Off(p.y, p0.y, PosTolerance) || Off(p.z, p0.z, PosTolerance) ||
                       Off(q.x, q0.x, RotTolerance) || Off(q.y, q0.y, RotTolerance) || Off(q.z, q0.z, RotTolerance) || Off(q.w, q0.w, RotTolerance);
            }
        }

        sealed class Part
        {
            public MeshRenderer Renderer;
            public Transform Transform;
            public GameObject GameObject;           // cached: Component.gameObject is a native call
            public Material Material;               // first shared material (checked every frame)
            public int[] Links;                     // own transform first, then the parents up to the root
            public ProbeBlend Blend;                // reflection probes this part gets (probe-sampling shaders only)
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
            public ProbeBlend Blend;                // non-null: the shader samples reflection probes, checked
            public MeshRenderer Renderer;           // the merged renderer
            public readonly List<int> PartIndices = new List<int>();
            public readonly List<CombineInstance> Instances = new List<CombineInstance>();
            public int Vertices;
        }

        /// <summary>
        /// The reflection probes Unity picks for a renderer (from its bounds, or its probe anchor) and their
        /// weights, as Renderer.GetClosestReflectionProbes reports them. Compared exactly (weights bitwise).
        /// </summary>
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

            /// <summary>True if the renderer gets exactly these probes and weights now (no allocation).</summary>
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

        /// <summary>Everything about a reflection probe that decides which renderers it affects.</summary>
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
        readonly Func<bool> eligible;
        readonly Func<Renderer, bool> exclude;
        readonly Dictionary<int, Dynamic> dynamicParts = new Dictionary<int, Dynamic>(); // instance ID -> moves
        readonly List<Part> parts = new List<Part>();
        readonly List<Link> links = new List<Link>();
        readonly List<GameObject> merged = new List<GameObject>();
        readonly List<MeshRenderer> mergedRenderers = new List<MeshRenderer>();
        readonly Dictionary<string, int> skipped = new Dictionary<string, int>();     // last observation, by reason
        readonly List<string> dynamicNames = new List<string>();                        // first moving parts (diagnostics)

        /// <summary>The first parts that were found moving relative to the body (console: mopr render).</summary>
        public string MovingSample() => dynamicNames.Count == 0 ? "-" : string.Join(", ", dynamicNames.ToArray());

        State state = State.Idle;
        float stateSince;
        string lastReason = "-";
        int merges, invalidations, rootMoves, sameValueWrites, vertices, lastBuildFrames;
        float lastBuildMs, lastBuildMaxSliceMs;
        bool flagsDirty;                             // the root moved: part flags are set and say nothing
        bool originalsShown;                         // A/B measurement only: merged state kept, originals drawn

        public bool IsMerged => state == State.Merged;
        public int PartCount => parts.Count;
        public int MeshCount => merged.Count;
        public string LastReason => lastReason;
        /// <summary>True if the last Validate compared every pose (the body moved: the slow path).</summary>
        public bool LastCheckFollowedBody { get; private set; }

        /// <param name="eligible">True while the vehicle may be drawn merged (checked often).</param>
        /// <param name="exclude">Extra exclusion for renderers the owner manages itself.</param>
        public MeshMerger(Transform root, string name, Func<bool> eligible, Func<Renderer, bool> exclude)
        {
            this.root = root;
            this.name = name;
            this.eligible = eligible;
            this.exclude = exclude;
        }

        /// <summary>Drives the state machine. Call from Update; Build runs as a coroutine on host.</summary>
        public void Tick(MonoBehaviour host)
        {
            if (root == null) return;
            float now = Time.realtimeSinceStartup;

            switch (state)
            {
                case State.Idle:
                    if (now - stateSince < 2f) return;      // let things settle after a change
                    if (!SafeEligible()) { stateSince = now; return; }
                    BeginObserve(now);
                    break;

                case State.Observing:
                    if (!SafeEligible()) { Abort("not eligible"); return; }
                    if (now - stateSince < ObserveSeconds) return;
                    host.StartCoroutine(Build());
                    break;

                case State.Merged:
                    if (!SafeEligible()) Unmerge("not eligible");
                    break;
            }
        }

        bool SafeEligible()
        {
            try { return eligible(); }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "MESH_MERGER_ELIGIBLE_" + name);
                return false;
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
                parts.Add(new Part { Renderer = r, Transform = r.transform, GameObject = r.gameObject, Material = r.sharedMaterial });
            }
            BuildLinks();
            state = State.Observing;
            stateSince = now;
        }

        /// <summary>Collects the transforms between every part and the root (shared ones once) and their poses.</summary>
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

        /// <summary>
        /// Drops the links no remaining part uses, keeping the poses snapshotted at observation time (the merged
        /// meshes were built from them).
        /// </summary>
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

        // ----- Reflection probes (5.1) -----------------------------------------------------------------
        // A shader that samples reflection probes gets them per renderer: Unity picks the probes and weights from
        // the renderer's bounds. A merged mesh has other bounds than its parts, so it is only used when it gets
        // exactly the same probes and weights as every one of its parts (checked at build). While merged, the
        // probes themselves are compared every frame (on, active, position, box, importance), and when the car
        // moved by more than the pose tolerance every probe group is checked again (merged mesh and parts). Any
        // difference hands the originals back; the next merge regroups by the new probes.

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
            // Inactive ones too (one could be switched on later). Prefab assets, if any, never change: harmless.
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

        /// <summary>Null if the reflection probes of every probe group are still exactly what they were.</summary>
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

        /// <summary>Compares every link; true if any changed (their Changed flags tell which).</summary>
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

        /// <summary>Pose check of one part's chain (used when only its own change flag is set).</summary>
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

        /// <summary>After CompareAllLinks: every part below a changed link moves.</summary>
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

        /// <summary>Null if the renderer can be merged, otherwise why not (counted for the report).</summary>
        string SkipReason(MeshRenderer r)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return "hidden";
            if (IsDynamic(r.GetInstanceID(), Time.realtimeSinceStartup)) return "moving";
            if (r.gameObject.name.StartsWith("MOP_")) return "MOPR";
            if (exclude != null && exclude(r)) return "managed";

            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0) return "no mesh";

            // Single-material parts only: the game swaps materials with PlayMaker's SetMaterial, and only the
            // first material can be checked every frame without allocating (sharedMaterials copies the array).
            if (mesh.subMeshCount != 1) return "multi-material";
            Material mat = r.sharedMaterial;
            // Opaque geometry only: transparent and alpha-tested materials depend on draw order.
            if (mat == null || mat.renderQueue != GeometryQueue) return "transparent";
            Material[] mats = r.sharedMaterials;
            if (mats == null || mats.Length != 1) return "multi-material";

            // Probes are sampled at the anchor / bounds centre of a renderer: one merged mesh has one centre.
            if (r.probeAnchor != null && (r.useLightProbes || SamplesReflectionProbes(mat))) return "probe anchor";
            if (r.useLightProbes && LightmapSettings.lightProbes != null && LightmapSettings.lightProbes.count > 0) return "light probes";

            // Panels that deform on impact rewrite their own mesh; items are toggled by MOP itself.
            if (r.GetComponent<Deformable>() != null) return "deformable";
            if (r.GetComponent<Items.ItemBehaviour>() != null) return "item";

            // CombineMeshes does not flip the winding of mirrored parts.
            if (Determinant3(r.transform.localToWorldMatrix) < 0f) return "mirrored";
            return null;
        }

        /// <summary>
        /// False only for Unity's legacy shaders: their lighting models (Lambert, BlinnPhong) take no reflection
        /// probe (the Reflective ones use their own cube map property). Everything else, the Standard shaders and any
        /// custom shader, is treated as sampling the probes the renderer gets.
        /// </summary>
        static bool SamplesReflectionProbes(Material m)
        {
            Shader s = m != null ? m.shader : null;
            return s == null || !s.name.StartsWith("Legacy Shaders/", StringComparison.Ordinal);
        }

        /// <summary>Determinant of the rotation/scale part (Unity 5.0 has no Matrix4x4.determinant).</summary>
        static float Determinant3(Matrix4x4 m)
        {
            return m.m00 * (m.m11 * m.m22 - m.m12 * m.m21)
                 - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20)
                 + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);
        }

        /// <summary>
        /// Pose of the part relative to the root, from the local values of its chain (independent of where the
        /// vehicle is in the world, so there is no float precision loss far from the origin).
        /// </summary>
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

            // Parts that moved relative to the body during the observation are not merged.
            CompareAllLinks();
            List<Part> stable = new List<Part>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p.Renderer == null || !p.Renderer.enabled || !p.GameObject.activeInHierarchy) continue; // just not now
                if (PartMoved(p)) { MarkDynamic(p.Renderer); continue; }
                stable.Add(p);
            }
            parts.Clear();
            parts.AddRange(stable);
            BuildLinks();                               // only the chains of the parts that will be merged

            // Group by everything that changes how a renderer is drawn. For shaders that sample reflection probes
            // that includes the probe setting and the exact probes and weights Unity picks for the part (5.1 fix:
            // the merged renderers had Unity's default "blend probes", so interior parts the game draws without
            // reflections reflected the realtime probe - the cabin got lighter or darker with the surroundings).
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
            HashSet<int> notMerged = new HashSet<int>();     // parts of groups whose merged mesh would get other probes
            List<Group> builtGroups = new List<Group>();

            // Build the meshes, a few per frame. Nothing is shown until everything is ready.
            List<GameObject> built = new List<GameObject>();
            float sliceStart = buildStart;              // the first slice includes the grouping above
            for (int i = 0; i < order.Count; i++)
            {
                GameObject go = null;
                try
                {
                    Group g = order[i];
                    Mesh combined = new Mesh { name = "MOP_Merged_" + name + "_" + i };
                    combined.CombineMeshes(g.Instances.ToArray(), true, true);
                    go = new GameObject("MOP_Merged_" + name + "_" + i);
                    go.layer = g.Layer;
                    go.transform.SetParent(root, false);
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
                        // The merged mesh (other bounds) would get other reflection probes: these parts stay as they are.
                        for (int k = 0; k < g.PartIndices.Count; k++) notMerged.Add(g.PartIndices[k]);
                        UnityEngine.Object.Destroy(combined);
                        UnityEngine.Object.Destroy(go);
                        go = null;
                    }
                    else
                    {
                        built.Add(go);
                        builtGroups.Add(g);
                    }
                }
                catch (Exception ex)
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    ExceptionManager.New(ex, false, "MESH_MERGER_BUILD_" + name);
                    DestroyAll(built);
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
                    if (state != State.Building || root == null) { DestroyAll(built); yield break; }
                }
            }

            // Anything changed while building? Then the merged meshes are already stale.
            if (state != State.Building || root == null || !SafeEligible())
            {
                DestroyAll(built);
                Abort("changed while building");
                yield break;
            }

            // Reflection probes: the merged renderer of a probe-sampling group must get exactly what each of its
            // parts gets (checked again now, frames after the grouping), and the probes must stay as they are.
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
                DestroyAll(built);
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
                    DestroyAll(built);
                    Abort("part changed while building");
                    yield break;
                }
            }
            lastBuildMs = busyMs;
            lastBuildMaxSliceMs = maxSliceMs;
            lastBuildFrames = Time.frameCount - buildStartFrame;

            // From now on the fast check uses the change flags: clear them (see Validate).
            for (int i = 0; i < parts.Count; i++) parts[i].Transform.hasChanged = false;
            root.hasChanged = false;
            flagsDirty = false;

            // Swap: merged meshes on, originals off (same frame, before rendering).
            merged.Clear();
            merged.AddRange(built);
            mergedRenderers.Clear();
            vertices = 0;
            for (int i = 0; i < merged.Count; i++)
            {
                MeshRenderer mr = merged[i].GetComponent<MeshRenderer>();
                mergedRenderers.Add(mr);
                mr.enabled = true;
                vertices += merged[i].GetComponent<MeshFilter>().sharedMesh.vertexCount;
            }
            for (int i = 0; i < parts.Count; i++) parts[i].Renderer.enabled = false;
            originalsShown = false;
            state = State.Merged;
            stateSince = Time.realtimeSinceStartup;
            merges++;
        }

        /// <summary>
        /// Called once per frame from LateUpdate, before any camera renders (never from a camera callback, see
        /// VehicleMeshMerge.LateUpdate). Restores the originals if anything about a merged part changed, so the
        /// change is drawn correctly in this very frame (in every camera).
        /// </summary>
        public void Validate()
        {
            if (state != State.Merged) return;
            if (root == null) { Release(); return; }

            // Fast path: Transform.hasChanged is set when the part or any of its parents is written. When the
            // vehicle itself moved, every flag is set, so then (and once after it stopped) every local pose is
            // compared instead.
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

            // Renderer.enabled is checked for an eighth of the parts per frame. Someone switching an original on
            // while merged draws the same surface twice with the same material for at most 8 frames, then the
            // originals are handed back. (Satsuma FSMs have no visibility actions and MOP unmerges before it
            // sets renderers itself, so this is a safety net.) Moves, hiding and material swaps are visible at
            // once, so those are checked for every part every frame.
            int enabledPhase = Time.frameCount & 7;

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
                if ((i & 7) == enabledPhase && r.enabled != originalsShown) { Unmerge("renderer enabled elsewhere"); return; }
            }
        }

        /// <summary>
        /// A/B measurement only (TestBot): keep the merged state but draw the original renderers instead of
        /// the merged meshes, so both can be timed in alternating blocks without rebuilding.
        /// </summary>
        public void SetOriginalsShown(bool shown)
        {
            if (state != State.Merged || shown == originalsShown) return;
            originalsShown = shown;
            for (int i = 0; i < mergedRenderers.Count; i++)
                if (mergedRenderers[i] != null) mergedRenderers[i].enabled = !shown;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Renderer != null) parts[i].Renderer.enabled = shown;
        }

        /// <summary>Restores the original renderers and drops the merged meshes.</summary>
        public void Unmerge(string reason)
        {
            lastReason = reason;
            if (state == State.Merged)
            {
                invalidations++;
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i].Renderer != null) parts[i].Renderer.enabled = true;
            }
            DestroyAll(merged);
            mergedRenderers.Clear();
            originalsShown = false;
            parts.Clear();
            links.Clear();
            probeGroups.Clear();
            probeStates.Clear();
            state = State.Idle;
            stateSince = Time.realtimeSinceStartup;
        }

        void Abort(string reason)
        {
            lastReason = reason;
            parts.Clear();
            links.Clear();
            probeGroups.Clear();
            probeStates.Clear();
            state = State.Idle;
            stateSince = Time.realtimeSinceStartup;
        }

        /// <summary>Unmerge without logging (scene teardown).</summary>
        public void Release()
        {
            if (state == State.Merged) Unmerge("released");
            else { DestroyAll(merged); Abort("released"); }
        }

        static void DestroyAll(List<GameObject> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                GameObject go = list[i];
                if (go == null) continue;
                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
                UnityEngine.Object.Destroy(go);
            }
            list.Clear();
        }

        public string Report()
        {
            return $"{name}: {state}, {parts.Count} parts ({links.Count} transforms checked) in {merged.Count} meshes ({vertices} vertices), " +
                   $"{DynamicNowCount()} moving parts skipped, {merges} merges, {invalidations} invalidations, " +
                   $"body moves followed: {rootMoves}, same-value writes: {sameValueWrites}, last build {lastBuildMs:0.0} ms over {lastBuildFrames} frames " +
                   $"(max {lastBuildMaxSliceMs:0.0} ms/frame), last reason: {lastReason}" +
                   (originalsShown ? ", A/B: originals shown" : "") + ", not merged: " + SkippedText();
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
