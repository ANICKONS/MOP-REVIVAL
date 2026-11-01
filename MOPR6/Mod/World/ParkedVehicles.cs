// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// world.vehicles (requirement 10.1, design §9.1 "physics" and "components"; from MOPR 5.x Vehicle.ToggleUnityCar):
// a vanilla vehicle that stands parked away from the player is frozen - its body kinematic, UnityCar's wheels and car
// dynamics off. A parked car still costs a lot in vanilla: UnityCar pushes the body every physics step (four wheel
// raycasts and forces), so the body never sleeps and PhysX keeps solving it with its contacts. Measured (world-map,
// 2026-10-30, 9 vehicles, 1 s A/B): living room -1.15 ms, yard -2.31 ms, driving -3.44 ms per frame.
// Parked means, all the time for 2 s:
//   * the player is not in it and is farther than FreezeDistance (thawed again inside 0.9 of it);
//   * its engine is off: the game enables Drivetrain when the engine starts and switches it off with the engine;
//   * it stands: body speed below 0.1 m/s and turning below 0.1 rad/s; the game has not made the body kinematic;
//   * nothing ties it to another body: no joint between it and another root (trailer hitch, tow rope - the
//     game's, Better MSC's), no tow hook "Attached";
//   * no traffic car (TRAFFIC, NPC_CARS) moves within TrafficRadius: a parked car must stay pushable.
// Not exact: the frozen body keeps its pose (a parked car in vanilla jitters by about a millimetre), the wheels stop
// computing. Everything goes through the ledger (PhysicsArbiter RbKinematic, BehaviourArbiter BehaviourEnabled):
// the save barrier, the main switch and "mopr restore" thaw every vehicle. When the game switches one of the held
// components itself (Gifu, Hayosiko: CarDynamics on when the player gets in), the whole vehicle thaws.
// Only vehicles whose FSMs on the root are all vanilla (inventory). The boat is not here: it floats by its FSMs.

using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MOPR6.Catalog;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;

namespace MOPR6.World
{
    public sealed class ParkedVehicles : Optimization, Render.IAbSwitch
    {
        public static readonly ParkedVehicles Instance = new ParkedVehicles();
        public const string SystemId = "world.vehicles";

        public const float FreezeDistance = 15f;
        const float ThawFactor = 0.9f;
        const float RestSpeed = 0.3f;
        const float RestTurn = 0.3f;
        const float EngineRpm = 100f;
        const double RestSeconds = 2;
        const double CooldownSeconds = 5;
        const float TrafficRadius = 25f;
        const double LinkSeconds = 2;

        static readonly string[] Roots =
        {
            "SATSUMA(557kg, 248)", "HAYOSIKO(1500kg, 250)", "JONNEZ ES(Clone)", "FLATBED", "KEKMET(350-400psi)",
            "RCO_RUSCKO12(270)", "FERNDALE(1630kg)", "GIFU(750/450psi)", "COMBINE(350-400psi)", "BOAT"
        };
        // FSMs on a vehicle's root that only simulate its body every frame (single state, every-frame actions, no
        // transitions): paused with it. The boat floats by them - "Y Velocity" sets the body's velocity from its
        // height, "Rise Front" turns the bow by the speed (SetRotation every frame, 0.23 ms in the yard).
        static readonly Dictionary<string, string[]> SimFsms = new Dictionary<string, string[]>
        {
            { "BOAT", new[] { "Y Velocity", "Rise Front" } }
        };
        static readonly string[] TrafficRoots = { "TRAFFIC", "NPC_CARS" };
        // Where the game itself moves or checks a parked car (MOPR 5.x SatsumaInArea): the repair shop's lifter, the
        // inspection, the rally's parc fermé. A vehicle inside one is never frozen.
        sealed class Area { public string Path; public Vector3 Offset; public float Radius; public Transform Anchor; }
        readonly Area[] areas =
        {
            new Area { Path = "REPAIRSHOP/Lifter/Platform", Radius = 6f },
            new Area { Path = "INSPECTION", Radius = 20f },
            new Area { Path = null, Offset = new Vector3(-1383f, 3f, 1260f), Radius = 20f }
        };
        // UnityCar's per-step simulation; the controller (input), lights and sound stay as they are.
        static readonly HashSet<string> SimTypes = new HashSet<string> { "CarDynamics", "Wheel", "AerodynamicResistance", "Wing" };

        sealed class Vehicle
        {
            public string Name;
            public GameObject Root;
            public Rigidbody Body;
            public int BodyId;
            public readonly List<Behaviour> Sim = new List<Behaviour>();
            public readonly List<int> SimIds = new List<int>();
            public readonly List<PlayMakerFSM> Fsms = new List<PlayMakerFSM>();
            public readonly List<int> FsmIds = new List<int>();
            public Drivetrain Drivetrain;
            public string Free = "-";      // why it is not frozen (report)
            public readonly List<FsmBool> Hooks = new List<FsmBool>();
            public double RestSince = -1, ThawedAt = -1e9;
            public bool Linked;
            public string LastThaw = "-";
        }

        readonly List<Vehicle> vehicles = new List<Vehicle>();
        readonly List<string> skipped = new List<string>();
        readonly Dictionary<string, long> thaws = new Dictionary<string, long>();
        readonly List<Rigidbody> traffic = new List<Rigidbody>();
        Transform player;
        bool scanned;
        double nextLinks;
        long freezes;

        ParkedVehicles() : base(SystemId) { }

        public override string Title => "Parked vehicles";
        public override Tier Tier => Tier.World;
        // On by default: equivalence full 0 unexplained with three explained differences (expected.json: the parked
        // tractor's speed jitter, the Gifu's IK tire angles, which Satsuma wheel last hit a bump on the drive);
        // 1 s A/B -1.7 ms living room, -4.1 ms yard, -1.4 ms driving (2026-10-30; Docs/world.ru.md).
        public override bool DefaultOn => true;

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!Vanilla.Ready) return "no vanilla inventory (" + Vanilla.Error + ")";
            nextLinks = 0;
            return null;
        }

        protected internal override void OnRemove()
        {
            abHeld = false;
            foreach (Vehicle v in vehicles) Thaw(v, null);
        }

        protected internal override void OnSuspend(string reason)
        {
            foreach (Vehicle v in vehicles) Thaw(v, null);
        }

        protected internal override void OnScene()
        {
            vehicles.Clear();
            skipped.Clear();
            traffic.Clear();
            player = null;
            scanned = false;
            abHeld = false;
        }

        void Scan()
        {
            scanned = true;
            foreach (Area a in areas)
            {
                if (a.Path == null) continue;
                GameObject go = GameObject.Find(a.Path);
                a.Anchor = go != null ? go.transform : null;
                if (a.Anchor == null) skipped.Add("area " + a.Path + ": not found");
            }
            foreach (string name in Roots)
            {
                GameObject go = GameObject.Find(name);
                if (go == null || go.transform.parent != null) { skipped.Add(name + ": not found"); continue; }
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb == null) { skipped.Add(name + ": no body"); continue; }
                string why = NotVanilla(go);
                if (why != null) { skipped.Add(name + ": " + why); continue; }
                Vehicle v = new Vehicle { Name = name, Root = go, Body = rb, BodyId = Arbiters.Id(rb) };
                foreach (Behaviour b in go.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null) continue;
                    string type = b.GetType().Name;
                    if (b is Drivetrain && b.transform == go.transform) v.Drivetrain = (Drivetrain)b;
                    if (!SimTypes.Contains(type)) continue;
                    v.Sim.Add(b);
                    v.SimIds.Add(Arbiters.Id(b));
                }
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != "HookFront" && t.name != "HookRear") continue;
                    foreach (PlayMakerFSM pm in t.GetComponents<PlayMakerFSM>())
                    {
                        FsmBool attached = pm.FsmVariables.FindFsmBool("Attached");
                        if (attached != null) v.Hooks.Add(attached);
                    }
                }
                string[] fsmNames;
                if (SimFsms.TryGetValue(name, out fsmNames))
                {
                    foreach (PlayMakerFSM pm in go.GetComponents<PlayMakerFSM>())
                    {
                        if (pm == null || Array.IndexOf(fsmNames, pm.FsmName) < 0) continue;
                        v.Fsms.Add(pm);
                        v.FsmIds.Add(Arbiters.Id(pm));
                    }
                    if (v.Fsms.Count != fsmNames.Length) { skipped.Add(name + ": its simulation FSMs not found"); continue; }
                }
                if (v.Sim.Count == 0 && v.Fsms.Count == 0) { skipped.Add(name + ": no UnityCar"); continue; }
                vehicles.Add(v);
            }
        }

        /// <summary>Null when every FSM on the vehicle's root is in the vanilla inventory.</summary>
        static string NotVanilla(GameObject go)
        {
            foreach (PlayMakerFSM pm in go.GetComponents<PlayMakerFSM>())
                if (pm != null && !Vanilla.For(pm).Value) return "FSM " + pm.FsmName + " not vanilla";
            return null;
        }

        protected internal override void OnTick(double now)
        {
            if (!scanned) Scan();
            if (player == null)
            {
                GameObject p = GameObject.Find("PLAYER");
                if (p == null) return;
                player = p.transform;
            }
            if (now >= nextLinks)
            {
                nextLinks = now + LinkSeconds;
                FindLinks();
                FindTraffic();
            }
            if (abHeld) return;
            Vector3 pp = player.position;
            Transform playerRoot = player.root;
            float freeze2 = FreezeDistance * FreezeDistance, thaw2 = freeze2 * ThawFactor * ThawFactor;
            for (int i = 0; i < vehicles.Count; i++)
            {
                Vehicle v = vehicles[i];
                if (v.Root == null || v.Body == null) { Thaw(v, null); vehicles.RemoveAt(i--); continue; }
                bool frozen = Arbiters.Physics.Has(v.BodyId, Id);
                float d2 = (v.Body.position - pp).sqrMagnitude;
                string block = Blocker(v, playerRoot, frozen ? thaw2 : freeze2, d2);
                if (frozen)
                {
                    if (block == null && AnyDropped(v)) block = "the game";
                    if (block == null) block = TrafficNear(v);
                    if (block != null) Thaw(v, block);
                    continue;
                }
                // The game took the body over (it wrote isKinematic): the components this system still holds go back.
                if (AnyHeld(v)) Thaw(v, "the game");
                if (block == null && now - v.ThawedAt < CooldownSeconds) block = "just thawed";
                if (block == null) block = Motion(v);
                if (block == null) block = TrafficNear(v);
                v.Free = block;
                if (block != null) { v.RestSince = -1; continue; }
                if (v.RestSince < 0) { v.RestSince = now; continue; }
                if (now - v.RestSince >= RestSeconds) Freeze(v, now);
            }
        }

        /// <summary>Null when the body stands (UnityCar keeps a parked car jittering by a few cm/s).</summary>
        static string Motion(Vehicle v)
        {
            float s = v.Body.velocity.magnitude, w = v.Body.angularVelocity.magnitude;
            if (s > RestSpeed) return "moving " + s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
            if (w > RestTurn) return "turning " + w.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " rad/s";
            return null;
        }

        /// <summary>Why the vehicle may not be (or stay) frozen now, apart from motion and traffic; null if nothing.</summary>
        string Blocker(Vehicle v, Transform playerRoot, float limit2, float d2)
        {
            // Switched off by world.deactivate: it stays frozen (that system needs it frozen and wakes it first).
            if (!v.Root.activeInHierarchy && !Dormancy.Instance.IsOff(v.Root)) return "switched off";
            if (playerRoot == v.Root.transform) return "player inside";
            if (d2 < limit2) return "player near";
            // Engine running: the game enables Drivetrain at the first start; the Satsuma keeps it on afterwards, so the
            // revolutions decide (an engine that is off winds down to 0).
            if (v.Drivetrain != null && v.Drivetrain.enabled && v.Drivetrain.rpm > EngineRpm) return "engine on";
            if (v.Linked) return "tied to another body";
            for (int i = 0; i < v.Hooks.Count; i++) if (v.Hooks[i].Value) return "tow hook";
            if (!Arbiters.Physics.Has(v.BodyId, Id) && v.Body.isKinematic) return "kinematic by the game";
            Vector3 p = v.Body.position;
            for (int i = 0; i < areas.Length; i++)
            {
                Area a = areas[i];
                Vector3 c = a.Path == null ? a.Offset : a.Anchor != null ? a.Anchor.position : new Vector3(float.NaN, 0, 0);
                if (!float.IsNaN(c.x) && (p - c).sqrMagnitude < a.Radius * a.Radius) return "special area";
            }
            return null;
        }

        /// <summary>A frozen vehicle holds every one of its sim components: one let go means the game wrote it.</summary>
        bool AnyDropped(Vehicle v)
        {
            for (int i = 0; i < v.SimIds.Count; i++)
                if (v.Sim[i] != null && !Arbiters.Behaviours.Has(v.SimIds[i], Id)) return true;
            for (int i = 0; i < v.FsmIds.Count; i++)
                if (v.Fsms[i] != null && !Arbiters.FsmSleep.Has(v.FsmIds[i], Id)) return true;
            return false;
        }

        bool AnyHeld(Vehicle v)
        {
            for (int i = 0; i < v.SimIds.Count; i++)
                if (Arbiters.Behaviours.Has(v.SimIds[i], Id)) return true;
            for (int i = 0; i < v.FsmIds.Count; i++)
                if (Arbiters.FsmSleep.Has(v.FsmIds[i], Id) || Arbiters.FsmPause.Has(v.FsmIds[i], Id)) return true;
            return false;
        }

        void Freeze(Vehicle v, double now)
        {
            if (Arbiters.Physics.Add(v.BodyId, Id) != SetResult.Ok) { v.RestSince = -1; return; }
            for (int i = 0; i < v.Sim.Count; i++)
            {
                if (v.Sim[i] == null) continue;
                if (Arbiters.Behaviours.Add(v.SimIds[i], Id) != SetResult.Ok) { Thaw(v, "behaviour refused"); return; }
            }
            // Simulation FSMs as the script sleep does it: RestartOnEnable off first (OnDisable keeps the state), then
            // the component off; their single state goes on where it was when they are enabled again.
            for (int i = 0; i < v.Fsms.Count; i++)
            {
                if (v.Fsms[i] == null) continue;
                if (Arbiters.FsmPause.Add(v.FsmIds[i], Id) != SetResult.Ok || Arbiters.FsmSleep.Add(v.FsmIds[i], Id) != SetResult.Ok)
                {
                    Thaw(v, "FSM refused");
                    return;
                }
            }
            v.RestSince = -1;
            freezes++;
        }

        void Thaw(Vehicle v, string why)
        {
            // In reverse order of Freeze: FSMs (enabled, then their restart flag), behaviours, the body.
            for (int i = v.FsmIds.Count - 1; i >= 0; i--)
            {
                Arbiters.FsmSleep.Remove(v.FsmIds[i], Id);
                Arbiters.FsmPause.Remove(v.FsmIds[i], Id);
            }
            for (int i = v.SimIds.Count - 1; i >= 0; i--) Arbiters.Behaviours.Remove(v.SimIds[i], Id);
            bool was = Arbiters.Physics.Has(v.BodyId, Id);
            Arbiters.Physics.Remove(v.BodyId, Id);
            if (!was || why == null) return;
            v.ThawedAt = MOPR6.Diagnostics.ModLog.Now();
            v.LastThaw = why;
            thaws[why] = (thaws.TryGetValue(why, out long n) ? n : 0) + 1;
        }

        /// <summary>Every joint of the scene between two different roots ties both (trailer hitch, tow ropes of any mod).</summary>
        void FindLinks()
        {
            for (int i = 0; i < vehicles.Count; i++) vehicles[i].Linked = false;
            if (vehicles.Count == 0) return;
            foreach (Joint j in UnityEngine.Object.FindObjectsOfType<Joint>())
            {
                if (j == null || j.connectedBody == null) continue;
                Transform a = j.transform.root, b = j.connectedBody.transform.root;
                if (a == b) continue;
                for (int i = 0; i < vehicles.Count; i++)
                {
                    Transform r = vehicles[i].Root != null ? vehicles[i].Root.transform : null;
                    if (r == a || r == b) vehicles[i].Linked = true;
                }
            }
        }

        void FindTraffic()
        {
            traffic.Clear();
            foreach (string name in TrafficRoots)
            {
                GameObject root = GameObject.Find(name);
                if (root == null) continue;
                foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(false))
                    if (rb != null && !rb.isKinematic && rb.mass >= 300f) traffic.Add(rb);
            }
        }

        string TrafficNear(Vehicle v)
        {
            float r2 = TrafficRadius * TrafficRadius;
            Vector3 p = v.Body.position;
            for (int i = 0; i < traffic.Count; i++)
            {
                Rigidbody t = traffic[i];
                if (t == null || t.isKinematic) continue;
                if ((t.position - p).sqrMagnitude < r2 && t.velocity.sqrMagnitude > 1f) return "traffic";
            }
            return null;
        }

        /// <summary>This system holds the body frozen now (render.merge merges the Satsuma only then).</summary>
        public bool IsFrozen(Rigidbody body)
        {
            if (body == null || Arbiters.Physics == null) return false;
            for (int i = 0; i < vehicles.Count; i++)
                if (ReferenceEquals(vehicles[i].Body, body)) return Arbiters.Physics.Has(vehicles[i].BodyId, Id);
            return false;
        }

        // ----- A/B (TestBot): off = every vehicle thawed and nothing frozen; on = every parked one frozen at once.
        bool abHeld;

        public string SetAb(bool on)
        {
            if (player == null) return "no player yet";
            if (!on)
            {
                abHeld = true;
                foreach (Vehicle v in vehicles) Thaw(v, null);
                return null;
            }
            abHeld = false;
            Transform playerRoot = player.root;
            float freeze2 = FreezeDistance * FreezeDistance;
            double now = MOPR6.Diagnostics.ModLog.Now();
            foreach (Vehicle v in vehicles)
            {
                if (v.Root == null || v.Body == null) continue;
                // The same conditions as the tick, without the 2 s of rest (a body thawed by A is still standing).
                if (Blocker(v, playerRoot, freeze2, (v.Body.position - player.position).sqrMagnitude) != null || Motion(v) != null || TrafficNear(v) != null) continue;
                Freeze(v, now);
            }
            return null;
        }

        public override void Report(StringBuilder sb)
        {
            int frozen = 0;
            foreach (Vehicle v in vehicles) if (Arbiters.Physics != null && Arbiters.Physics.Has(v.BodyId, Id)) frozen++;
            sb.Append("  ").Append(vehicles.Count).Append(" vehicles, ").Append(frozen).Append(" frozen now; freezes ").Append(freezes).Append(", thaws: ");
            foreach (KeyValuePair<string, long> kv in thaws) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append(", ");
            sb.Append('\n');
            foreach (Vehicle v in vehicles)
                sb.Append("    ").Append(v.Name).Append(": ").Append(Arbiters.Physics != null && Arbiters.Physics.Has(v.BodyId, Id) ? "frozen" : "free (" + v.Free + ")")
                  .Append(", ").Append(v.Sim.Count).Append(" sim components, ").Append(v.Fsms.Count).Append(" sim FSMs, last thaw ").Append(v.LastThaw).Append('\n');
            if (skipped.Count > 0) sb.Append("  skipped: ").Append(string.Join("; ", skipped.ToArray())).Append('\n');
        }
    }
}
