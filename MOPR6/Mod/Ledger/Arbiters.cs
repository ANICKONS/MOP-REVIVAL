// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The one ledger of the game scene and its arbiters (design §3). Systems ask an arbiter (Render.Add(renderer,
// "render.layers")), never the ledger and never Unity directly. The table of design §3:
//   Render        RendererEnabled = false          Shadows       ShadowCasting = Off
//   Lights        LightEnabled = false             LampShadows   LightShadows = None
//   CameraCull    CameraLayerCull (computed)       Spherical     CameraCullSpherical = true
//   FsmSleep      FsmEnabled = false               FsmPause      FsmRestartOnEnable = false
//   Physics       RbKinematic = true               Behaviours    BehaviourEnabled = false
//   Colliders     ColliderEnabled = false          Activation    GoActive = false (world.deactivate, task 50)
// Fixed values are reason arbiters (several systems may want the same), computed values are value arbiters (one
// holder per property). Activation comes before the FSM arbiters: RemoveSystem gives back in this order, and an
// object must be on again before the restart flags of its paused FSMs come back (design §9.2).
// Background check (mechanism 3): every 2 s a slice of the held entries is compared with what was written; drift is
// External (the arbiter lets go, the game's value stays) and a log line names the property.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using MOPR6.Core.Ledger;
using MOPR6.Diagnostics;
using UnityEngine;

namespace MOPR6.Ledger
{
    public static class Arbiters
    {
        public static readonly UnityAccess Access = new UnityAccess();
        public static ChangeLedger Ledger { get; private set; }

        public static ReasonArbiter Render, Shadows, Lights, LampShadows, Spherical, Created, Activation, FsmSleep, FsmPause, Physics, Behaviours, Colliders;

        /// <summary>
        /// Above 0 while the ledger gives objects and FSMs back (RestoreAll, RemoveSystem, a part of the world switched on
        /// again): every PlayMakerFSM.OnEnable meanwhile is a return to where the FSM was, and the script sleep puts it
        /// back at its place in PlayMaker's list (OrderKeeper, design §7.4) instead of stamping it as new.
        /// </summary>
        public static int Restoring;
        public static ValueArbiter CameraCull;
        static readonly List<ReasonArbiter> all = new List<ReasonArbiter>();
        static readonly List<ValueArbiter> values = new List<ValueArbiter>();
        static int driftCursor;

        /// <summary>Entries found changed by somebody else in the background check (this scene).</summary>
        public static int Drifted { get; private set; }

        /// <summary>RestoreAll calls so far (systems that keep their own state see that everything went back).</summary>
        public static int Restores { get; private set; }

        /// <summary>Right after RestoreAll gave everything back (every scene; systems subscribe once).</summary>
        public static event System.Action RestoredAll;

        /// <summary>A new game scene: a new ledger (the old objects are gone).</summary>
        public static void StartScene(bool debugBuild)
        {
            Access.Clear();
            all.Clear();
            values.Clear();
            driftCursor = 0;
            Drifted = 0;
            Ledger = new ChangeLedger(Access, ModLog.Line) { ThrowOnConflict = debugBuild };
            Render = Add("RenderArbiter", PropKind.RendererEnabled, PropValue.Bool(false));
            Shadows = Add("ShadowArbiter", PropKind.ShadowCasting, PropValue.Int((int)UnityEngine.Rendering.ShadowCastingMode.Off));
            Lights = Add("LightArbiter", PropKind.LightEnabled, PropValue.Bool(false));
            LampShadows = Add("LampShadowArbiter", PropKind.LightShadows, PropValue.Int((int)LightShadows.None));
            Spherical = Add("CameraSphericalArbiter", PropKind.CameraCullSpherical, PropValue.Bool(true));
            // Objects MOPR6 made (merged meshes): held = they exist; given back = destroyed (after the originals).
            Created = Add("CreatedObjects", PropKind.CreatedObject, PropValue.Bool(true));
            Activation = Add("ActivationArbiter", PropKind.GoActive, PropValue.Bool(false));
            FsmSleep = Add("FsmControl.sleep", PropKind.FsmEnabled, PropValue.Bool(false));
            FsmPause = Add("FsmControl.pause", PropKind.FsmRestartOnEnable, PropValue.Bool(false));
            Physics = Add("PhysicsArbiter", PropKind.RbKinematic, PropValue.Bool(true));
            Behaviours = Add("BehaviourArbiter", PropKind.BehaviourEnabled, PropValue.Bool(false));
            Colliders = Add("ColliderArbiter", PropKind.ColliderEnabled, PropValue.Bool(false));
            CameraCull = AddValue("CameraCullArbiter", PropKind.CameraLayerCull);
        }

        static ReasonArbiter Add(string owner, PropKind kind, PropValue value)
        {
            ReasonArbiter a = new ReasonArbiter(Ledger, owner, kind, value);
            a.Dropped += (key, systems) => ModLog.Line(owner + ": the game took over " + Access.Describe(key.Target) + " from " + string.Join(", ", systems.ToArray()));
            all.Add(a);
            return a;
        }

        /// <summary>A value arbiter lost a property to the game (every scene; systems subscribe once).</summary>
        public static event System.Action<PropKey, string> ValueDropped;

        static ValueArbiter AddValue(string owner, PropKind kind)
        {
            ValueArbiter a = new ValueArbiter(Ledger, owner, kind);
            a.Dropped += (key, system) =>
            {
                ModLog.Line(owner + ": the game took over " + Access.Describe(key.Target) + " from " + system);
                if (ValueDropped != null) ValueDropped(key, system);
            };
            values.Add(a);
            return a;
        }

        public static int Id(Object o) => Access.Register(o);

        public static void RemoveSystem(string system)
        {
            Restoring++;
            try
            {
                foreach (ReasonArbiter a in all) a.RemoveSystem(system);
                foreach (ValueArbiter a in values) a.RemoveSystem(system);
            }
            finally { Restoring--; }
        }

        /// <summary>Everything back (master switch, save barrier). Returns the records that did not simply go back.</summary>
        public static List<RestoreRecord> RestoreAll(string why)
        {
            List<RestoreRecord> odd = new List<RestoreRecord>();
            if (Ledger == null) return odd;
            List<RestoreRecord> records;
            Restoring++;
            try { records = Ledger.RestoreAll(); }
            finally { Restoring--; }
            Restores++;
            if (RestoredAll != null)
            {
                try { RestoredAll(); }
                catch (System.Exception ex) { ModLog.Report("ledger/RestoredAll", ex); }
            }
            foreach (ReasonArbiter a in all) a.Forget();
            foreach (ValueArbiter a in values) a.Forget();
            foreach (RestoreRecord r in records)
            {
                if (r.Outcome == RestoreOutcome.Restored) continue;
                odd.Add(r);
                ModLog.Line("restore (" + why + "): " + Access.Describe(r.Key.Target) + "." + r.Key.Kind + " " + r.Outcome +
                            ", found " + r.Found + ", original " + r.Original);
            }
            ModLog.Line("restore (" + why + "): " + records.Count + " properties, " + odd.Count + " not restored");
            return odd;
        }

        /// <summary>The background check: up to <paramref name="budget"/> entries (host, every 2 s).</summary>
        public static void CheckDrift(int budget)
        {
            if (Ledger == null || Ledger.Count == 0 || Ledger.Frozen) return;
            Drifted += Ledger.CheckDrift(budget, ref driftCursor);
        }

        /// <summary>"path.kind original -> applied owner state" per entry, for the TestBot (TestApi.LedgerDump).</summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            if (Ledger == null) return "";
            foreach (LedgerEntry e in Ledger.Entries)
                sb.Append(Access.Describe(e.Key.Target)).Append('.').Append(e.Key.Kind).Append('\t').Append(e.Original).Append(" -> ")
                  .Append(e.Applied).Append('\t').Append(e.Owner).Append('\t').Append(e.State).Append('\n');
            return sb.ToString();
        }

        /// <summary>Held entries per owner, for the report ("ShadowArbiter 812, LampShadowArbiter 14").</summary>
        public static string Summary()
        {
            if (Ledger == null || Ledger.Count == 0) return "empty";
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (LedgerEntry e in Ledger.Entries) n[e.Owner] = (n.TryGetValue(e.Owner, out int c) ? c : 0) + 1;
            return string.Join(", ", n.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value).ToArray()) +
                   (Drifted > 0 ? "; drifted " + Drifted : "");
        }
    }
}
