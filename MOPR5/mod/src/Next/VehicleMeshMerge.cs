// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Drives the MeshMerger for the Satsuma (5.1): decides when the car counts as parked, validates the merge
// every frame in LateUpdate (before any camera renders), and hands the original renderers back whenever MOP
// needs them (renderer culling, saving, settings, MOP stopping).

using System;
using System.Collections;
using UnityEngine;

using MOP.Common;
using MOP.Vehicles.Cases;

namespace MOP.Next
{
    internal class VehicleMeshMerge : MonoBehaviour
    {
        static VehicleMeshMerge instance;
        MeshMerger satsuma;
        bool ready;
        static int validatedFrame = -1;

        /// <summary>The merger's own setting and the render section's main switch (it lives in that section).</summary>
        internal static bool Enabled =>
            (MopMenu.RenderOptimizerEnabled == null || MopMenu.RenderOptimizerEnabled.GetValue()) &&
            (MopMenu.RenderMergeVehicles == null || MopMenu.RenderMergeVehicles.GetValue());

        void Awake()
        {
            instance = this;
        }

        /// <summary>
        /// The merge is validated in LateUpdate: after the game's scripts, physics and animation have moved the
        /// parts, before any camera (mirrors and reflection probe included) renders the frame.
        ///
        /// Never from a camera callback (Camera.onPreCull and the like). Unity 5.0 loses a renderer that is
        /// switched on while a camera renders: Renderer.enabled reads true, but the renderer is never drawn
        /// again until it is switched off and on outside rendering. Measured with the TestBot (render-toggle,
        /// satsuma-doors): validating in onPreCull left the parts that moved (tyres, seat backs, suspension)
        /// invisible after an unmerge, and every other merged part invisible until the next merge.
        ///
        /// A part written later in the frame (another script's LateUpdate) keeps its change flag and is caught
        /// in the next frame: one frame of a stale merged mesh at worst.
        /// </summary>
        void LateUpdate()
        {
            int f = Time.frameCount;
            if (f == validatedFrame) return;
            validatedFrame = f;
            ValidateNow();
        }

        IEnumerator Start()
        {
            // Same start condition as the render optimizer: MOP finished loading and the car settled.
            while (Hypervisor.Instance == null || !Hypervisor.Instance.IsItemInitializationDone())
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(3f);

            Satsuma car = Satsuma.Instance;
            if (car != null && !car.IsPassive && car.gameObject != null)
            {
                // Renderers whose shadow mode the render optimizer manages keep their own draw call (one owner).
                satsuma = new MeshMerger(car.transform, "Satsuma", SatsumaEligible,
                    r => RenderOptimizer.Instance != null && RenderOptimizer.Instance.IsShadowManaged(r));
            }
            ready = true;
        }

        static bool SatsumaEligible()
        {
            if (!Enabled || !MopSettings.IsModActive) return false;
            Satsuma car = Satsuma.Instance;
            return car != null && car.IsMergeable();
        }

        void Update()
        {
            if (!ready || satsuma == null) return;
            try
            {
                satsuma.Tick(this);
                satsuma.SetOriginalsShown(AbDrawOriginals);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "VEHICLE_MESH_MERGE_UPDATE");
                SafeRelease();
                satsuma = null; // stop for this session; the originals are back
            }
        }

        // Cost of the per-frame validation while merged (reported by "mopr render"): [0] body still, [1] body moved.
        readonly long[] validateTicks = new long[2];
        readonly int[] validateFrames = new int[2];

        void ValidateNow()
        {
            if (satsuma == null) return;
            try
            {
                // A/B "originals" blocks skip the check too, so the A/B measures the merger's net effect. Change
                // flags (hasChanged) persist, so the first check after switching back still sees any change.
                if (!satsuma.IsMerged || AbDrawOriginals) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                satsuma.Validate();
                int k = satsuma.LastCheckFollowedBody ? 1 : 0;
                validateTicks[k] += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                validateFrames[k]++;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "VEHICLE_MESH_MERGE_VALIDATE");
                SafeRelease();
                satsuma = null;
            }
        }

        void SafeRelease()
        {
            try { satsuma?.Release(); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "VEHICLE_MESH_MERGE_RELEASE"); }
        }

        void OnDestroy()
        {
            SafeRelease();
            if (instance == this) instance = null;
        }

        // ----- Called by MOP --------------------------------------------------------------------------

        /// <summary>MOP is about to set the car's renderers itself: give them back first.</summary>
        internal static void UnmergeSatsuma(string reason)
        {
            if (instance == null || instance.satsuma == null) return;
            try { instance.satsuma.Unmerge(reason); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "VEHICLE_MESH_MERGE_UNMERGE"); }
        }

        /// <summary>Before saving: every original renderer back on, merged meshes gone.</summary>
        internal static void ReleaseAll()
        {
            if (instance != null) instance.SafeRelease();
        }

        /// <summary>Settings callback.</summary>
        internal static void OnSettingsChanged()
        {
            if (!Enabled) UnmergeSatsuma("disabled in settings");
            // Applies in game right away: the shadow LOD takes the car's parts back (off) or hands them over (on).
            RenderOptimizer.OnSatsumaOwnerChanged();
        }

        internal static bool IsSatsumaMerged => instance != null && instance.satsuma != null && instance.satsuma.IsMerged;

        /// <summary>Diagnostics: the first parts found moving relative to the body.</summary>
        internal static string MovingSample() => instance != null && instance.satsuma != null ? instance.satsuma.MovingSample() : "-";

        /// <summary>
        /// A/B measurement only (set by the TestBot through reflection): while true, a merged car is drawn with
        /// its original renderers, so merged and original can be timed in alternating blocks. Never saved.
        /// </summary>
        internal static bool AbDrawOriginals { get; set; }

        internal static string Report()
        {
            if (instance == null) return "Mesh merger: not running";
            if (instance.satsuma == null) return "Mesh merger: " + (Enabled ? "no Satsuma to merge" : "disabled");
            return "Mesh merger: " + (Enabled ? "" : "(disabled) ") + instance.satsuma.Report() +
                   $", check {instance.CheckMicroseconds(0):0.0} us/frame ({instance.validateFrames[0]} frames), " +
                   $"{instance.CheckMicroseconds(1):0.0} us when the body moved ({instance.validateFrames[1]} frames)";
        }

        double CheckMicroseconds(int k)
        {
            return validateFrames[k] > 0 ? validateTicks[k] * 1e6 / System.Diagnostics.Stopwatch.Frequency / validateFrames[k] : 0;
        }
    }
}
