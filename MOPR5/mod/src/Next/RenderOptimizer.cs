// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Render optimizer: purely visual optimizations that are safe by construction.
//
// Design rules (learned from the history of MOP and the 4.x revival):
//   1. It NEVER calls SetActive, never changes Renderer/Light/Behaviour.enabled, never moves objects,
//      never touches physics or PlayMaker. Game logic cannot observe anything it does.
//   2. It is the ONLY owner of the properties it changes (camera layer cull distances, shadowCastingMode
//      of small renderers, shadows of distant lamps). Lamps that MOP's Yard place manages are excluded.
//   3. Every change is recorded with the original value. RestoreAll() puts everything back and runs
//      before saving, when the feature is disabled and when the component is destroyed.
//   4. If the game or another mod changes a value we manage, we adopt the new value as the original
//      and release ownership instead of fighting over it.
//   5. All distances are derived from real object sizes and the current camera, so nothing that is
//      larger than a few pixels on screen is ever culled.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

using MOP.Common;
using MOP.Places;
using MOP.Managers;

namespace MOP.Next
{
    class RenderOptimizer : MonoBehaviour
    {
        public static RenderOptimizer Instance { get; private set; }

        // ----- Tunables -----------------------------------------------------------------------------
        // Screen-space size (in pixels) below which a small object stops being drawn, per quality level.
        static readonly float[] CullPixels = { 4f, 2f, 1f };          // Aggressive, Balanced, Quality
        // Screen-space size (in pixels) below which a small object stops casting a shadow.
        static readonly float[] ShadowPixels = { 12f, 6f, 3f };
        // Extra distance beyond a lamp's range after which its real-time shadows are switched off.
        static readonly float[] LampShadowMargin = { 15f, 35f, 70f };

        const float MinCullDistance = 30f;          // never cull closer than this
        const float MinShadowDistance = 10f;        // never drop shadows closer than this
        const float SmallObjectMaxSize = 2.5f;      // only objects smaller than this get shadow LOD
        const int ShadowEntriesPerFrame = 250;      // time slicing of the shadow LOD pass
        const float CameraCheckInterval = 0.5f;
        const float LampCheckInterval = 1f;
        const int LayerCount = 32;
        const int UILayer = 5;

        // ----- Layer culling state ----------------------------------------------------------------------
        Camera managedCamera;
        float[] originalLayerDistances;
        bool originalSpherical;
        bool layerCullingApplied;
        readonly float[] layerMaxSize = new float[LayerCount];
        readonly bool[] layerCullable = new bool[LayerCount];
        readonly int[] layerRendererCount = new int[LayerCount];
        float lastFov = -1f;
        int lastScreenHeight = -1;
        int lastQuality = -1;

        // ----- Shadow LOD state -------------------------------------------------------------------------
        sealed class ShadowEntry
        {
            public Renderer Renderer;
            public Transform Transform;
            public float SqrDistance;      // distance (squared) beyond which the shadow is dropped
            public float Size;
            public bool OwnedOff;          // we switched shadowCastingMode to Off
        }
        readonly List<ShadowEntry> shadowEntries = new List<ShadowEntry>();
        int shadowCursor;
        int shadowOwnedCount;

        // ----- Lamp shadows state -----------------------------------------------------------------------
        sealed class LampEntry
        {
            public Light Light;
            public Transform Transform;
            public LightShadows Original;
            public bool OwnedOff;
            public float SqrDistance;
        }
        readonly List<LampEntry> lampEntries = new List<LampEntry>();
        int lampOwnedCount;
        float nextLampCheck;

        float nextCameraCheck;
        bool scanned;
        bool active;

        // ------------------------------------------------------------------------------------------------

        void Awake()
        {
            Instance = this;
        }

        IEnumerator Start()
        {
            // Wait until MOP finished its own loading (it reparents objects and creates LOD dummies).
            while (Hypervisor.Instance == null || !Hypervisor.Instance.IsItemInitializationDone())
                yield return new WaitForSeconds(1f);

            yield return new WaitForSeconds(2f);
            Rescan();
            ApplySettings();

            // Mods often spawn their objects a bit later: pick them up once more after a minute
            // (incremental: nothing is restored or re-applied, only new objects are added).
            yield return new WaitForSeconds(60f);
            if (this != null && scanned)
            {
                try { RescanIncremental(); }
                catch (Exception ex) { ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_INCREMENTAL_SCAN"); }
            }
        }

        /// <summary>
        /// Adds renderers and lamps that appeared since the full scan (active scene objects only) and
        /// grows the per-layer size statistics, so newly spawned big objects never get culled early.
        /// </summary>
        public void RescanIncremental()
        {
            Renderer[] sceneRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < sceneRenderers.Length; i++)
            {
                UpdateLayerStats(sceneRenderers[i]);
                TryAddShadowCandidate(sceneRenderers[i]);
            }

            HashSet<Light> known = new HashSet<Light>();
            for (int i = 0; i < lampEntries.Count; i++)
                if (lampEntries[i].Light != null) known.Add(lampEntries[i].Light);
            HashSet<Light> yardLights = GetYardManagedLights();
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
                TryAddLamp(lights[i], known, yardLights);

            RecomputeShadowDistances();
            // Grown layer statistics are applied by the next camera check in Update (only while active).
        }

        bool layerStatsDirty;

        /// <summary>Feeds a renderer into the per-layer statistics (size can only grow).</summary>
        void UpdateLayerStats(Renderer r)
        {
            if (r == null) return;
            int layer = r.gameObject.layer;
            if (layer < 0 || layer >= LayerCount || !layerCullable[layer]) return;

            bool isMesh = r is MeshRenderer;
            bool isSkinned = r is SkinnedMeshRenderer;
            if (!isMesh && !isSkinned)
            {
                layerCullable[layer] = false;
                layerStatsDirty = true;
                return;
            }

            float size = EstimateSize(r, isSkinned);
            if (size < 0f)
            {
                layerCullable[layer] = false;
                layerStatsDirty = true;
            }
            else if (size > layerMaxSize[layer])
            {
                layerMaxSize[layer] = size;
                layerStatsDirty = true;
            }

            if (layerRendererCount[layer] == 0)
            {
                layerRendererCount[layer] = 1;
                layerStatsDirty = true;
            }
        }

        void TryAddLamp(Light l, HashSet<Light> known, HashSet<Light> yardLights)
        {
            if (l == null || l.type == LightType.Directional || l.shadows == LightShadows.None) return;
            if (yardLights.Contains(l) || (known != null && known.Contains(l))) return;
            if (IsPassiveSatsumaObject(l.transform)) return;
            lampEntries.Add(new LampEntry { Light = l, Transform = l.transform, Original = l.shadows, SqrDistance = LampDistanceFor(l) });
        }

        float LampDistanceFor(Light l)
        {
            float d = l.range + LampShadowMargin[Quality];
            return d * d;
        }

        /// <summary>
        /// With "satsuma_ignore" the Satsuma belongs to another mod: never touch its renderers or lamps.
        /// MOP - REVIVAL 5.1: with the mesh merger on, the Satsuma's renderers belong to the merger (one owner
        /// per property): the shadow LOD leaves them alone, so a merged part never keeps a stale shadow mode.
        /// </summary>
        static bool IsPassiveSatsumaObject(Transform t)
        {
            if (t == null) return false;
            bool ignore = Rules.RulesManager.Book.SatsumaExclude;
            bool merger = MopMenu.RenderMergeVehicles == null || MopMenu.RenderMergeVehicles.GetValue();
            if (!ignore && !merger) return false;
            Vehicles.Cases.Satsuma satsuma = Vehicles.Cases.Satsuma.Instance;
            return satsuma != null && satsuma.transform != null && t.root == satsuma.transform;
        }

        /// <summary>Settings callback (MSCLoader). Safe to call from the main menu.</summary>
        public static void OnSettingsChanged()
        {
            if (Instance != null)
                Instance.ApplySettings();
        }

        bool scannedWithMerger;

        /// <summary>
        /// MOP - REVIVAL 5.1: the Satsuma mesh merger was switched on or off in game. Its parts belong to one
        /// owner (see IsPassiveSatsumaObject), so the shadow LOD hands them over right away instead of after the
        /// next load: rescan (every shadow and lamp value it changed is restored first, then rebuilt).
        /// </summary>
        internal static void OnSatsumaOwnerChanged()
        {
            RenderOptimizer o = Instance;
            if (o == null || !o.scanned) return;
            bool merger = MopMenu.RenderMergeVehicles == null || MopMenu.RenderMergeVehicles.GetValue();
            if (merger == o.scannedWithMerger) return;
            try
            {
                float t0 = Time.realtimeSinceStartup;
                o.Rescan();
                o.ApplySettings();
                ModConsole.Debug($"Render optimizer: the Satsuma's shadows are now handled by the " +
                                 $"{(merger ? "mesh merger" : "shadow LOD")} ({(Time.realtimeSinceStartup - t0) * 1000f:0} ms).");
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_SATSUMA_OWNER");
                o.SafeRestoreAll();
            }
        }

        /// <summary>Re-evaluates all features from the current settings.</summary>
        public void ApplySettings()
        {
            try
            {
                bool enabledAll = MopMenu.RenderOptimizerEnabled != null && MopMenu.RenderOptimizerEnabled.GetValue();
                active = enabledAll && scanned;

                if (!active)
                {
                    RestoreAll();
                    return;
                }

                if (!MopMenu.RenderLayerCulling.GetValue()) RestoreLayerCulling();
                else UpdateLayerCulling(true);

                if (!MopMenu.RenderShadowLod.GetValue()) RestoreShadows();
                else RecomputeShadowDistances();

                if (!MopMenu.RenderLightShadows.GetValue()) RestoreLamps();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_APPLY");
                SafeRestoreAll();
            }
        }

        int Quality => MopMenu.RenderCullQuality != null ? Mathf.Clamp(MopMenu.RenderCullQuality.GetValue(), 0, 2) : 1;

        // ------------------------------------------------------------------------------------------------
        // Scanning
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Collects renderer sizes per layer and the renderers/lamps eligible for shadow LOD.
        /// Includes inactive objects (MOP may have disabled them) so the statistics stay conservative.
        /// </summary>
        public void Rescan()
        {
            // Release everything we own before rebuilding the lists.
            RestoreShadows();
            RestoreLamps();
            shadowEntries.Clear();
            shadowManagedIds.Clear();
            lampEntries.Clear();
            shadowCursor = 0;

            for (int i = 0; i < LayerCount; i++)
            {
                layerMaxSize[i] = 0f;
                layerCullable[i] = i != UILayer;
                layerRendererCount[i] = 0;
            }

            Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;

                GameObject go = r.gameObject;
                int layer = go.layer;
                if (layer < 0 || layer >= LayerCount) continue;

                layerRendererCount[layer]++;

                bool isMesh = r is MeshRenderer;
                bool isSkinned = r is SkinnedMeshRenderer;
                if (!isMesh && !isSkinned)
                {
                    // Particles, lines, trails, sprites... their size is not knowable: never cull that layer.
                    layerCullable[layer] = false;
                    continue;
                }

                float size = EstimateSize(r, isSkinned);
                if (size < 0f)
                {
                    layerCullable[layer] = false;
                    continue;
                }

                if (size > layerMaxSize[layer])
                    layerMaxSize[layer] = size;
            }

            // Shadow LOD candidates. Resources.FindObjectsOfTypeAll also returns prefab assets, and changing a
            // prefab would leak into every future instance, so candidates only come from scene objects:
            // active scene renderers + the renderers of every item MOP manages (active or not).
            trackedRenderers.Clear();
            Renderer[] sceneRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < sceneRenderers.Length; i++)
                TryAddShadowCandidate(sceneRenderers[i]);
            knownItems.Clear();
            ScanNewItems();

            // Lamps with real-time shadows, except the ones MOP's Yard place manages.
            HashSet<Light> yardLights = GetYardManagedLights();
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
                TryAddLamp(lights[i], null, yardLights);

            scanned = true;
            scannedWithMerger = MopMenu.RenderMergeVehicles == null || MopMenu.RenderMergeVehicles.GetValue();
            layerStatsDirty = true;
            lastQuality = -1;
            RecomputeShadowDistances();
            ModConsole.Debug($"Render optimizer: {all.Length} renderers analysed, {shadowEntries.Count} small shadow casters, {lampEntries.Count} lamps.");

            // The renderer statistics include every prefab and inactive object: release the big array now.
            all = null;
        }

        readonly HashSet<int> trackedRenderers = new HashSet<int>();
        readonly HashSet<int> knownItems = new HashSet<int>();
        float nextItemScan;
        const float ItemScanInterval = 5f;

        void TryAddShadowCandidate(Renderer r)
        {
            if (r == null || !(r is MeshRenderer)) return;
            if (r.shadowCastingMode != ShadowCastingMode.On) return;
            if (!trackedRenderers.Add(r.GetInstanceID())) return;

            string name = r.gameObject.name;
            if (name.StartsWith("MOP_") || name.StartsWith("DUMMY_")) return; // MOP's own helper / LOD objects
            if (IsPassiveSatsumaObject(r.transform)) return;

            float size = EstimateSize(r, false);
            if (size <= 0f || size > SmallObjectMaxSize) return;

            ShadowEntry e = new ShadowEntry { Renderer = r, Transform = r.transform, Size = size };
            e.SqrDistance = ShadowDistanceFor(size);
            shadowEntries.Add(e);
            shadowManagedIds.Add(r.GetInstanceID());
        }

        // MOP - REVIVAL 5.1: renderers whose shadow mode this optimizer manages (the mesh merger skips them).
        readonly HashSet<int> shadowManagedIds = new HashSet<int>();

        internal bool IsShadowManaged(Renderer r) => r != null && shadowManagedIds.Contains(r.GetInstanceID());

        /// <summary>Adds renderers of items MOP registered since the last scan (bought, spawned...).</summary>
        void ScanNewItems()
        {
            if (ItemsManager.Instance == null) return;
            List<Items.ItemBehaviour> items = ItemsManager.Instance.GetAll;
            for (int i = 0; i < items.Count; i++)
            {
                Items.ItemBehaviour item = items[i];
                if (item == null) continue;
                if (!knownItems.Add(item.GetInstanceID())) continue;

                Renderer[] rs = item.GetComponentsInChildren<Renderer>(true);
                for (int j = 0; j < rs.Length; j++)
                {
                    UpdateLayerStats(rs[j]);
                    TryAddShadowCandidate(rs[j]);
                }
            }
        }

        float ShadowDistanceFor(float size)
        {
            Camera cam = Camera.main;
            float fov = cam != null ? cam.fieldOfView : 60f;
            float pixelsPerMeterAtOneMeter = Screen.height / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            float d = Mathf.Max(MinShadowDistance, size * pixelsPerMeterAtOneMeter / ShadowPixels[Quality]);
            return d * d;
        }

        /// <summary>World-space diagonal of the renderer, or -1 if it cannot be determined.</summary>
        static float EstimateSize(Renderer r, bool isSkinned)
        {
            try
            {
                if (r.enabled && r.gameObject.activeInHierarchy)
                    return r.bounds.size.magnitude;

                // Inactive renderer: bounds are not up to date, use the mesh bounds scaled to world space.
                Mesh mesh = null;
                if (isSkinned)
                {
                    mesh = ((SkinnedMeshRenderer)r).sharedMesh;
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                if (mesh == null) return -1f;
                return Vector3.Scale(mesh.bounds.size, r.transform.lossyScale).magnitude;
            }
            catch
            {
                return -1f;
            }
        }

        static HashSet<Light> GetYardManagedLights()
        {
            HashSet<Light> set = new HashSet<Light>();
            try
            {
                if (PlaceManager.Instance == null) return set;
                foreach (Place place in PlaceManager.Instance.GetAll)
                {
                    if (place == null || place.LightSources == null) continue;
                    foreach (Light l in place.LightSources)
                        if (l != null) set.Add(l);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_YARD_LIGHTS");
            }
            return set;
        }

        // ------------------------------------------------------------------------------------------------
        // Per-frame work
        // ------------------------------------------------------------------------------------------------

        void Update()
        {
            if (!active) return;

            try
            {
                float now = Time.realtimeSinceStartup;

                if (now >= nextCameraCheck)
                {
                    nextCameraCheck = now + CameraCheckInterval;
                    if (MopMenu.RenderLayerCulling.GetValue())
                        UpdateLayerCulling(false);
                }

                Camera cam = managedCamera != null ? managedCamera : Camera.main;
                if (cam == null) return;
                Vector3 camPos = cam.transform.position;

                if (MopMenu.RenderShadowLod.GetValue())
                {
                    if (now >= nextItemScan)
                    {
                        nextItemScan = now + ItemScanInterval;
                        ScanNewItems();
                    }
                    TickShadows(camPos);
                }

                if (MopMenu.RenderLightShadows.GetValue() && now >= nextLampCheck)
                {
                    nextLampCheck = now + LampCheckInterval;
                    TickLamps(camPos);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_UPDATE");
                SafeRestoreAll();
                active = false;
            }
        }

        // ----- Layer culling ----------------------------------------------------------------------------

        void UpdateLayerCulling(bool force)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            if (cam != managedCamera)
            {
                RestoreLayerCulling();
                managedCamera = cam;
                float[] current = cam.layerCullDistances;
                originalLayerDistances = current != null && current.Length == LayerCount ? (float[])current.Clone() : new float[LayerCount];
                originalSpherical = cam.layerCullSpherical;
                force = true;
            }
            else if (layerCullingApplied && lastWrittenDistances != null)
            {
                // Rule 4: if the game or another mod wrote its own distances since our last write,
                // adopt them as the new originals instead of overwriting them.
                float[] current = cam.layerCullDistances;
                if (!SameDistances(current, lastWrittenDistances))
                {
                    originalLayerDistances = current != null && current.Length == LayerCount ? (float[])current.Clone() : new float[LayerCount];
                    force = true;
                }
                if (forceSpherical && !cam.layerCullSpherical)
                {
                    // Someone wants planar culling: respect it from now on.
                    originalSpherical = false;
                    forceSpherical = false;
                }
            }

            if (layerStatsDirty)
            {
                layerStatsDirty = false;
                force = true;
            }

            float fov = cam.fieldOfView;
            int height = Screen.height;
            int quality = Quality;
            if (!force && Mathf.Approximately(fov, lastFov) && height == lastScreenHeight && quality == lastQuality && layerCullingApplied)
                return;

            lastFov = fov;
            lastScreenHeight = height;
            lastQuality = quality;

            float pixelsPerMeterAtOneMeter = height / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            float threshold = CullPixels[quality];

            float[] distances = new float[LayerCount];
            bool any = false;
            for (int i = 0; i < LayerCount; i++)
            {
                float result = originalLayerDistances[i];
                if (layerCullable[i] && layerRendererCount[i] > 0 && layerMaxSize[i] > 0f)
                {
                    float d = Mathf.Max(MinCullDistance, layerMaxSize[i] * pixelsPerMeterAtOneMeter / threshold);
                    // Beyond any realistic draw distance a cull distance is pointless: leave the layer alone.
                    if (d < MaxUsefulCullDistance)
                    {
                        result = result > 0f ? Mathf.Min(result, d) : d;
                        any = true;
                    }
                }
                distances[i] = result;
            }

            if (!any)
            {
                RestoreLayerCulling();
                return;
            }

            cam.layerCullDistances = distances;
            if (forceSpherical) cam.layerCullSpherical = true; // no popping at the screen edges when turning
            lastWrittenDistances = distances;
            layerCullingApplied = true;
        }

        const float MaxUsefulCullDistance = 4000f;
        float[] lastWrittenDistances;
        bool forceSpherical = true;

        static bool SameDistances(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i] - b[i]) > 0.01f) return false;
            return true;
        }

        void RestoreLayerCulling()
        {
            if (!layerCullingApplied) return;
            layerCullingApplied = false;
            lastWrittenDistances = null;
            if (managedCamera == null) return;

            managedCamera.layerCullDistances = originalLayerDistances ?? new float[LayerCount];
            managedCamera.layerCullSpherical = originalSpherical;
        }

        // ----- Shadow LOD -------------------------------------------------------------------------------

        void RecomputeShadowDistances()
        {
            for (int i = 0; i < shadowEntries.Count; i++)
            {
                ShadowEntry e = shadowEntries[i];
                e.SqrDistance = ShadowDistanceFor(e.Size);
            }

            for (int i = 0; i < lampEntries.Count; i++)
            {
                LampEntry l = lampEntries[i];
                if (l.Light == null) continue;
                l.SqrDistance = LampDistanceFor(l.Light);
            }
        }

        void TickShadows(Vector3 camPos)
        {
            int count = shadowEntries.Count;
            if (count == 0) return;

            int budget = Mathf.Min(ShadowEntriesPerFrame, count);
            for (int n = 0; n < budget; n++)
            {
                if (shadowCursor >= shadowEntries.Count) shadowCursor = 0;
                ShadowEntry e = shadowEntries[shadowCursor];

                if (e.Renderer == null)
                {
                    if (e.OwnedOff) shadowOwnedCount--;
                    shadowEntries.RemoveAt(shadowCursor);
                    if (shadowEntries.Count == 0) return;
                    continue;
                }

                shadowCursor++;

                bool far = (e.Transform.position - camPos).sqrMagnitude > e.SqrDistance;
                ShadowCastingMode current = e.Renderer.shadowCastingMode;

                if (e.OwnedOff)
                {
                    if (current != ShadowCastingMode.Off)
                    {
                        // Someone else changed it: adopt their value, release ownership.
                        e.OwnedOff = false;
                        shadowOwnedCount--;
                        continue;
                    }

                    if (!far)
                    {
                        e.Renderer.shadowCastingMode = ShadowCastingMode.On;
                        e.OwnedOff = false;
                        shadowOwnedCount--;
                    }
                }
                else if (far && current == ShadowCastingMode.On)
                {
                    e.Renderer.shadowCastingMode = ShadowCastingMode.Off;
                    e.OwnedOff = true;
                    shadowOwnedCount++;
                }
            }
        }

        void RestoreShadows()
        {
            for (int i = 0; i < shadowEntries.Count; i++)
            {
                ShadowEntry e = shadowEntries[i];
                if (!e.OwnedOff) continue;
                e.OwnedOff = false;
                try
                {
                    if (e.Renderer != null && e.Renderer.shadowCastingMode == ShadowCastingMode.Off)
                        e.Renderer.shadowCastingMode = ShadowCastingMode.On;
                }
                catch { }
            }
            shadowOwnedCount = 0;
        }

        // ----- Lamp shadows -----------------------------------------------------------------------------

        void TickLamps(Vector3 camPos)
        {
            for (int i = lampEntries.Count - 1; i >= 0; i--)
            {
                LampEntry l = lampEntries[i];
                if (l.Light == null)
                {
                    if (l.OwnedOff) lampOwnedCount--;
                    lampEntries.RemoveAt(i);
                    continue;
                }

                bool far = (l.Transform.position - camPos).sqrMagnitude > l.SqrDistance;
                LightShadows current = l.Light.shadows;

                if (l.OwnedOff)
                {
                    if (current != LightShadows.None)
                    {
                        // The game re-enabled the shadows itself (e.g. its graphics options): adopt it.
                        l.Original = current;
                        l.OwnedOff = false;
                        lampOwnedCount--;
                        continue;
                    }

                    if (!far)
                    {
                        l.Light.shadows = l.Original;
                        l.OwnedOff = false;
                        lampOwnedCount--;
                    }
                }
                else
                {
                    if (current == LightShadows.None) continue; // the game turned them off, nothing to do
                    l.Original = current;
                    if (far)
                    {
                        l.Light.shadows = LightShadows.None;
                        l.OwnedOff = true;
                        lampOwnedCount++;
                    }
                }
            }
        }

        void RestoreLamps()
        {
            for (int i = 0; i < lampEntries.Count; i++)
            {
                LampEntry l = lampEntries[i];
                if (!l.OwnedOff) continue;
                l.OwnedOff = false;
                try
                {
                    if (l.Light != null && l.Light.shadows == LightShadows.None)
                        l.Light.shadows = l.Original;
                }
                catch { }
            }
            lampOwnedCount = 0;
        }

        // ------------------------------------------------------------------------------------------------
        // Restore
        // ------------------------------------------------------------------------------------------------

        /// <summary>Puts every value this component changed back to its original.</summary>
        public void RestoreAll()
        {
            RestoreLayerCulling();
            RestoreShadows();
            RestoreLamps();
        }

        void SafeRestoreAll()
        {
            try { RestoreAll(); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "RENDER_OPTIMIZER_RESTORE"); }
        }

        /// <summary>Called by the save barrier: restore and stop until the scene is reloaded.</summary>
        public void OnPreSave()
        {
            Suspend();
        }

        /// <summary>Restores everything and stops working until the settings are changed again.</summary>
        public void Suspend()
        {
            active = false;
            SafeRestoreAll();
        }

        void OnDestroy()
        {
            SafeRestoreAll();
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------------------------------------

        public string GetReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Render optimizer: ").Append(active ? "active" : "inactive").Append(", scanned: ").Append(scanned).AppendLine();
            Camera cam = managedCamera;
            sb.Append("Camera: ").Append(cam != null ? cam.name : "none")
              .Append(", FOV ").Append(lastFov.ToString("0.0"))
              .Append(", layer culling applied: ").Append(layerCullingApplied).AppendLine();

            if (cam != null && layerCullingApplied)
            {
                float[] d = cam.layerCullDistances;
                for (int i = 0; i < LayerCount && d != null && i < d.Length; i++)
                {
                    if (layerRendererCount[i] == 0) continue;
                    string name = LayerMask.LayerToName(i);
                    sb.Append("  [").Append(i).Append("] ").Append(string.IsNullOrEmpty(name) ? "?" : name)
                      .Append(": renderers ").Append(layerRendererCount[i])
                      .Append(", max size ").Append(layerMaxSize[i].ToString("0.00")).Append(" m")
                      .Append(", cull at ").Append(d[i] > 0 ? d[i].ToString("0") + " m" : "far plane")
                      .Append(layerCullable[i] ? "" : " (not cullable)").AppendLine();
                }
            }

            sb.Append("Shadow LOD: ").Append(shadowEntries.Count).Append(" tracked, ").Append(shadowOwnedCount).AppendLine(" without shadow right now");
            sb.Append("Lamp shadows: ").Append(lampEntries.Count).Append(" tracked, ").Append(lampOwnedCount).AppendLine(" disabled right now");
            return sb.ToString();
        }

        public int ShadowOwnedCount => shadowOwnedCount;
        public int LampOwnedCount => lampOwnedCount;
        public bool IsActive => active;
    }
}
