// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// render.merge (task 33; ported from MOPR 5.x Next/VehicleMeshMerge.cs): draws the parked Satsuma as merged meshes
// (MeshMerger). When the car counts as parked, without the 5.x vehicle manager:
//   * the car is active and slower than 0.5 m/s;
//   * the player is not in it (the player is a child of the car while seated: measured in 5.x, merging saves
//     nothing from the driver's seat and the check costs 0.2 ms);
//   * the AI driver is off, and the car is not at the inspection or in the rally parc fermé (other code moves it
//     there; same as 5.x).
// The merge is checked every frame in LateUpdate before any camera renders (MeshMerger.Validate), built in slices
// of 2 ms, and given back when the car stops counting as parked, at the save barrier, with the main switch.
// A/B for the TestBot (SetAb): the merge stays, only what is drawn switches (the originals or the merged meshes).

using System.Text;
using MOPR6.Core.Host;
using MOPR6.Diagnostics;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Render
{
    public sealed class SatsumaMerge : Optimization, IAbSwitch
    {
        public static readonly SatsumaMerge Instance = new SatsumaMerge();
        public const string SystemId = "render.merge";
        internal const string SatsumaPath = "SATSUMA(557kg, 248)";

        // Areas where other code moves the car (5.x Hypervisor: SatsumaInArea triggers).
        static readonly Vector3 ParcFermeCenter = new Vector3(-1383f, 3f, 1260f), ParcFermeHalf = new Vector3(20.5f, 6f, 17.5f);
        static readonly Vector3 InspectionHalf = new Vector3(10f, 10f, 10f);

        Transform root;
        Rigidbody body;
        Transform player;
        Transform inspection;
        PlayMakerFSM aiDriver;
        MeshMerger merger;
        GameObject hostObject;
        MergeHost host;
        bool showOriginals;
        long validateTicks, validateFrames, validateTicksMoving, validateFramesMoving;

        SatsumaMerge() : base(SystemId) { }

        public override string Title => "Satsuma merge";
        public override Tier Tier => Tier.Render;
        // On by default since the parked Satsuma is frozen (world.vehicles): -0.37 ms from the yard (25 m), the picture
        // differs only in the FPS counter (stage4-home, 2026-10-31); alone (2026-10-29) -0.13 ms. Equivalence: 7 of 7
        // scenarios (equiv-20261031-151515 and the re-run equiv-20261031-153718 of the two with one-off differences,
        // which the merge could not have caused - it was not merged then; Docs/world.ru.md).
        public override bool DefaultOn => true;

        protected internal override string OnInstall(SystemMode mode)
        {
            GameObject car = GameObject.Find(SatsumaPath);
            if (car == null) return "no Satsuma in the scene";
            root = car.transform;
            body = car.GetComponent<Rigidbody>();
            GameObject p = GameObject.Find("PLAYER");
            player = p != null ? p.transform : null;
            GameObject insp = GameObject.Find("INSPECTION");
            inspection = insp != null ? insp.transform : null;
            Transform ai = root.Find("AI");
            aiDriver = null;
            if (ai != null)
                foreach (PlayMakerFSM f in ai.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "Driving") aiDriver = f;
            merger = new MeshMerger(root, "Satsuma", Id, Eligible);
            hostObject = new GameObject("MOPR_MergeHost");
            host = hostObject.AddComponent<MergeHost>();
            host.Owner = this;
            showOriginals = false;
            return null;
        }

        protected internal override void OnRemove()
        {
            if (merger != null) merger.Release();
            merger = null;
            if (hostObject != null) Object.Destroy(hostObject);
            hostObject = null;
            host = null;
        }

        protected internal override void OnSuspend(string reason)
        {
            if (merger != null) merger.Release();
        }

        protected internal override void OnScene()
        {
            // The old scene's objects are gone; an installed system finds the new car at its next install.
            merger = null;
            hostObject = null;
            host = null;
            root = null;
        }

        protected internal override void OnTick(double now)
        {
            if (merger == null && State.Status == SystemStatus.Installed && root == null)
            {
                // A new scene with the system installed: set up again for the new car.
                if (OnInstall(State.Mode) != null) return;
            }
            if (merger == null || host == null) return;
            merger.Tick(host);
            merger.SetOriginalsShown(showOriginals);
        }

        bool Eligible()
        {
            if (root == null || !root.gameObject.activeInHierarchy) return false;
            // With the parked vehicles on, only while it holds the Satsuma frozen (15 m and more away): the body
            // stands still, so the pose check is cheap (0.11 ms instead of 0.34 ms), and the merged body is far.
            if (body != null && World.ParkedVehicles.Instance.Live && !World.ParkedVehicles.Instance.IsFrozen(body)) return false;
            if (body != null && body.velocity.sqrMagnitude > 0.25f) return false;
            if (player != null && player.root == root) return false;
            if (aiDriver != null && aiDriver.enabled) return false;
            Vector3 pos = root.position;
            if (inspection != null && Inside(pos, inspection.position, InspectionHalf)) return false;
            if (Inside(pos, ParcFermeCenter, ParcFermeHalf)) return false;
            return true;
        }

        static bool Inside(Vector3 p, Vector3 c, Vector3 half) =>
            Mathf.Abs(p.x - c.x) <= half.x && Mathf.Abs(p.y - c.y) <= half.y && Mathf.Abs(p.z - c.z) <= half.z;

        void LateTick()
        {
            if (!Live || merger == null || !merger.IsMerged || showOriginals) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { merger.Validate(); }
            catch (System.Exception ex) { Fail("Validate", ex, SatsumaPath); return; }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (merger.LastCheckFollowedBody) { validateTicksMoving += dt; validateFramesMoving++; }
            else { validateTicks += dt; validateFrames++; }
        }

        /// <summary>A/B: false = the originals are drawn (the merge stays built).</summary>
        public string SetAb(bool on)
        {
            showOriginals = !on;
            if (merger != null) merger.SetOriginalsShown(showOriginals);
            return null;
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(merger != null ? merger.Report() : "no merger (" + (root == null ? "no Satsuma" : "-") + ")")
              .Append("; check ").Append(Us(validateTicks, validateFrames)).Append(" us/frame (").Append(validateFrames).Append(" frames), ")
              .Append(Us(validateTicksMoving, validateFramesMoving)).Append(" us when the body moved (").Append(validateFramesMoving).Append(" frames)")
              .Append(showOriginals ? ", A/B: originals" : "").Append('\n');
        }

        static string Us(long ticks, long frames) =>
            frames > 0 ? (ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "0";

        sealed class MergeHost : MonoBehaviour
        {
            public SatsumaMerge Owner;
            int frame = -1;

            void LateUpdate()
            {
                int f = Time.frameCount;
                if (f == frame || Owner == null) return;
                frame = f;
                Owner.LateTick();
            }
        }
    }

    /// <summary>A system with its own A/B switch for the TestBot (TestApi.SetAb).</summary>
    public interface IAbSwitch
    {
        /// <summary>on = the system's effect is shown; off = what the game draws without it. Null or an error.</summary>
        string SetAb(bool on);
    }
}
