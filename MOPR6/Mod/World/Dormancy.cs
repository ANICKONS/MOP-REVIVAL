// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// world.deactivate (requirement 10.2, design §9.2, task 50): parked vehicles far from the player are switched off whole
// with SetActive(false), and a still copy of them is drawn instead (StandIn), so the picture stays. The Satsuma is not
// one of them (its own systems), nor the train, NPCs and traffic (they move: switching them off changes the game).
// The places of MOPR 5.x were tried first with a partial cover (only subtrees nothing needs): 0 errors, but no gain at
// all (±0.07 ms) - the game switches most of their content off by itself, and the rest works (Docs/world.ru.md). The
// pass below still supports a partial cover (Def.Whole = false) for that reason.
//
// A vehicle goes off only when it is frozen by world.vehicles (engine off, standing, nothing tied to it, no traffic
// near, not in a special area), the player has been farther than 125 m for 10 s, and a pass over its hierarchy finds
// no "keeper" - one per bug class 4.x/5.x had:
//   FSM busy and changing, timer running, action taking events, delayed event, waits for a Unity message, active lately,
//   not started, not vanilla, sub-FSMs        it does something now or gets something no hook can see (5.x: events
//                                             lost, timers stopped). A busy FSM that only polls is allowed when its
//                                             state and its variables (not those written by Get* actions: distances,
//                                             positions) stayed the same for 10 s with no state entry: the game would
//                                             not have changed it either
//   sound playing (audible), animation playing, particles, light, animator, camera, character controller, scripts
//   with OnEnable/OnDisable/coroutines, visibility read
//                                             Unity would stop or reset it, or the game reads what an inactive object
//                                             cannot answer
//   body awake, hinge open, joint of another kind, trigger in use
//                                             a hinge is made again at its current angle when the object comes back
//                                             (5.x: HingeManager), a foreign body in a trigger would enter it again
// Before SetActive(false): the stand-in is built (ledger CreatedObject), foreign bodies touching the vehicle are made
// kinematic (PhysicsArbiter: items in the van do not fall, R10.3), every FSM is paused (FsmPause: RestartOnEnable
// false, so OnDisable keeps its state - 5.x: FSMs starting over and reading the save). Back on, in the same frame:
// SetActive(true), the stand-in gone, the items thawed, silent looping sounds playing again, playOnAwake sounds that
// were silent stopped again, the restart flags back. Everything that can reach a vehicle wakes it first
// (DormantIndex); so does the player within 112 m, a thaw of world.vehicles, the game moving its root or taking a held
// item over. Everything is in the ledger: the save barrier, the main switch and "mopr restore" switch every vehicle
// on in that order.
// Verify: the passes run and the wake index is kept, nothing is switched off; a state entry of an FSM of a vehicle
// "off" without a wake is a mismatch (a missed path).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MOPR6.Catalog;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Core.World;
using MOPR6.Diagnostics;
using MOPR6.Engine;
using MOPR6.Host;
using MOPR6.Ledger;
using MOPR6.Render;
using MOPR6.Sleep;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MOPR6.World
{
    public sealed class Dormancy : Optimization, Render.IAbSwitch
    {
        public static readonly Dormancy Instance = new Dormancy();
        public const string SystemId = "world.deactivate";
        const string HarmonyId = "mopr.world.deactivate";
        const double QuietSeconds = 10;
        const float PixelLimit = 2f;
        const float TriggerMargin = 3f;
        const float SilentVolume = 0.001f;
        const float HingeDegrees = 1f;
        const double BodiesSeconds = 10, RetrySeconds = 30, WholeRetrySeconds = 5, FindSeconds = 30;
        const double ReuseCoverSeconds = 120;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Def
        {
            public readonly string Path;
            public readonly float Off;
            public readonly bool Whole;
            public Def(string path, float off, bool whole) { Path = path; Off = off; Whole = whole; }
        }

        // Distance of MOPR 5.x for vehicles: 125 m.
        static readonly Def[] Defs =
        {
            new Def("HAYOSIKO(1500kg, 250)", 125, true), new Def("JONNEZ ES(Clone)", 125, true), new Def("FLATBED", 125, true),
            new Def("KEKMET(350-400psi)", 125, true), new Def("RCO_RUSCKO12(270)", 125, true), new Def("FERNDALE(1630kg)", 125, true),
            new Def("GIFU(750/450psi)", 125, true), new Def("COMBINE(350-400psi)", 125, true), new Def("BOAT", 125, true)
        };

        internal sealed class Watch
        {
            public FsmState State;
            public int Hash;
            public double Since;
            public bool Cycled;     // a timed state: the loop went round at least once with nothing changed
        }

        /// <summary>A joint of a vehicle before it went off: compared one tick after it is on again (the joint is made anew).</summary>
        internal sealed class JointRecord
        {
            public Joint Joint;
            public float Angle;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        internal sealed class Unit
        {
            public string Name;
            public float Off, DefaultOff;
            public bool Whole;
            public GameObject Root;
            public Transform RootT;
            public Rigidbody Body;
            public bool Dormant, Virtual, Logged;
            public string Why = "-";
            public double DormantSince, DormantTotal, PassAt = -1e9, NextPass;
            // The cover of the last pass.
            public readonly List<GameObject> CoverRoots = new List<GameObject>();
            public readonly List<int> CoverIds = new List<int>();
            public readonly List<PlayMakerFSM> PauseFsms = new List<PlayMakerFSM>();
            public readonly List<int> PauseIds = new List<int>();
            public readonly List<GameObject> Objects = new List<GameObject>();
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<AudioSource> AudioGuard = new List<AudioSource>();
            public readonly List<AudioSource> Replay = new List<AudioSource>();
            public readonly List<ParticleSystem> ParticleGuard = new List<ParticleSystem>();
            public readonly List<Bounds> Triggers = new List<Bounds>();
            public Bounds TriggerBox;
            // A whole vehicle: what the stand-in draws, the solid colliders, the foreign bodies held while it is off.
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Projector> Projectors = new List<Projector>();
            public readonly List<Collider> Solids = new List<Collider>();
            public readonly List<Rigidbody> Frozen = new List<Rigidbody>();
            public readonly List<int> FrozenIds = new List<int>();
            public readonly Dictionary<Fsm, Watch> Watches = new Dictionary<Fsm, Watch>(new RefComparer<Fsm>());
            public readonly List<Joint> Joints = new List<Joint>();
            public readonly List<JointRecord> JointRecords = new List<JointRecord>();
            public double JointCheckAt = -1;
            public string JointBlock;      // a joint came back other than it went: never off again this session
            public StandIn Stand;
            public string StandText = "-";
            public Vector3 OffPos;
            public Quaternion OffRot;
            // The wake index (built when the part goes off).
            public readonly List<KeyValuePair<Fsm, FsmEvent[]>> IndexFsms = new List<KeyValuePair<Fsm, FsmEvent[]>>();
            public readonly HashSet<FsmEvent> Events = new HashSet<FsmEvent>(new RefComparer<FsmEvent>());
            // Statistics.
            public int Nodes, CoverNodes, Content, CoverContent, Fsms, BusyFsms;
            public readonly Dictionary<string, int> Keepers = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Busy = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<string> Samples = new List<string>();
            public double PassMs, OffMs, OnMs, MaxOffMs, MaxOnMs;
            public long Offs, Ons;
            public readonly Dictionary<string, long> WakeReasons = new Dictionary<string, long>(StringComparer.Ordinal);

            public Vector3 Anchor => Body != null ? Body.position : RootT.position;

            /// <summary>A cover root lies under <paramref name="t"/> (an event to an ancestor with its children).</summary>
            public bool CoverUnder(Transform t)
            {
                for (int i = 0; i < CoverRoots.Count; i++)
                    if (CoverRoots[i] != null && CoverRoots[i].transform.IsChildOf(t)) return true;
                return false;
            }
        }

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        readonly List<Unit> units = new List<Unit>();
        readonly List<Def> missing = new List<Def>();
        readonly List<string> skipped = new List<string>();
        readonly DormancyPolicy policy = new DormancyPolicy();
        readonly Dictionary<string, long> wakeReasons = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly List<Unit> candidates = new List<Unit>();
        Transform player;
        HarmonyInstance harmony;
        bool scanned, verify, abHeld, logTransitions = true;
        int cursor, restoresSeen;
        double nextFind, nextBodies;
        long offs, ons, gone;
        Rigidbody[] bodies = new Rigidbody[0];
        Vector3[] lastPos = new Vector3[0];

        Dormancy() : base(SystemId)
        {
            // The ledger gave everything back (restore, or the harness before a snapshot): the vehicles are on now, so
            // nothing may count as off from here on - not even until the next tick (their FSMs run again).
            Arbiters.RestoredAll += () =>
            {
                restoresSeen = Arbiters.Restores;
                ForgetRestored(ModLog.Now());
            };
        }

        public override string Title => "Distant vehicles off";
        public override Tier Tier => Tier.World;
        public override long Mismatches => DormantIndex.Mismatches;

        // ----- Install / remove -------------------------------------------------------------------------------

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!Vanilla.Ready) return "no vanilla inventory (" + Vanilla.Error + ")";
            if (!FsmAccess.Init()) return "field access unavailable (" + FsmAccess.Error + ")";
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo owner = typeof(Fsm).GetMethod("GetOwnerDefaultTarget", I, null, new[] { typeof(FsmOwnerDefault) }, null);
            MethodInfo find = typeof(FindGameObject).GetMethod("OnEnter", I | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            MethodInfo ownerHook = typeof(DormantIndex).GetMethod(nameof(DormantIndex.OwnerTargetPrefix), S);
            MethodInfo findHook = typeof(DormantIndex).GetMethod(nameof(DormantIndex.FindGameObjectPrefix), S);
            if (owner == null || find == null || ownerHook == null || findHook == null) return "PlayMaker methods not found";
            verify = mode == SystemMode.Verify;
            harmony = HarmonyInstance.Create(HarmonyId);
            harmony.Patch(owner, new HarmonyMethod(ownerHook), null, null);
            harmony.Patch(find, new HarmonyMethod(findHook), null, null);
            DormantIndex.ResetCounters();
            DormantIndex.Tracking = true;
            restoresSeen = Arbiters.Restores;
            offs = ons = 0;
            wakeReasons.Clear();
            return null;
        }

        protected internal override void OnRemove()
        {
            abHeld = false;
            WakeAll("removed", false);
            DormantIndex.Tracking = false;
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
        }

        protected internal override void OnSuspend(string reason) { WakeAll(reason, false); }

        protected internal override void OnScene()
        {
            // The objects of the old scene are gone: nothing to switch on.
            units.Clear();
            missing.Clear();
            skipped.Clear();
            candidates.Clear();
            DormantIndex.Clear();
            policy.Clear();
            fsmStatic.Clear();
            excludedVars.Clear();
            bodies = new Rigidbody[0];
            lastPos = new Vector3[0];
            player = null;
            scanned = abHeld = false;
            cursor = 0;
            nextFind = nextBodies = 0;
            restoresSeen = Arbiters.Restores;
        }

        void Scan(double now)
        {
            scanned = true;
            missing.AddRange(Defs);
            FindMissing(now);
        }

        void FindMissing(double now)
        {
            nextFind = now + FindSeconds;
            for (int k = 0; k < missing.Count; k++)
            {
                Def d = missing[k];
                GameObject go = GameObject.Find(d.Path);
                if (go == null) continue;
                missing.RemoveAt(k--);
                string why = null;
                Rigidbody body = go.GetComponent<Rigidbody>();
                if (d.Whole)
                {
                    if (go.transform.parent != null) why = "not a root";
                    else if (body == null) why = "no body";
                }
                for (int i = 0; why == null && i < units.Count; i++)
                    if (go.transform.IsChildOf(units[i].RootT) || units[i].RootT.IsChildOf(go.transform)) why = "inside " + units[i].Name;
                if (why != null) { skipped.Add(d.Path + ": " + why); continue; }
                units.Add(new Unit
                {
                    Name = go.name, Off = testDistance > 0 ? testDistance : d.Off, DefaultOff = d.Off, Whole = d.Whole, Root = go, RootT = go.transform,
                    Body = d.Whole ? body : null
                });
            }
        }

        // ----- Tick ---------------------------------------------------------------------------------------------

        protected internal override void OnTick(double now)
        {
            if (!FsmSleep.Instance.Live || !FsmSleep.HooksInstalled)
            {
                // The wake paths are the hooks of the script sleep: without them nothing may stay off.
                if (DormantIndex.Units > 0) WakeAll("script sleep is off", false);
                return;
            }
            if (!scanned) Scan(now);
            else if (missing.Count > 0 && now >= nextFind) FindMissing(now);
            if (player == null)
            {
                GameObject p = GameObject.Find("PLAYER");
                if (p == null) return;
                player = p.transform;
            }
            if (Arbiters.Restores != restoresSeen)
            {
                restoresSeen = Arbiters.Restores;
                ForgetRestored(now);
            }
            Vector3 pp = player.position;
            candidates.Clear();
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u.Root == null)
                {
                    if (u.Dormant) { DormantIndex.Unregister(u); u.Dormant = false; if (u.Stand != null) { u.Stand.Release(); u.Stand = null; } }
                    units.RemoveAt(i--);
                    gone++;
                    continue;
                }
                float d = (u.Anchor - pp).magnitude;
                if (u.Dormant) { CheckDormant(u, d, now); continue; }
                if (u.JointCheckAt >= 0 && now >= u.JointCheckAt) JointCheck(u);
                string why = policy.MayDeactivate(u.Name, d, u.Off, now) ?? Precondition(u) ?? (now < u.NextPass ? u.Why : null);
                if (why != null) { u.Why = why; continue; }
                candidates.Add(u);
            }
            CheckMovers(now);
            if (abHeld || candidates.Count == 0) return;
            // One pass per tick: a vehicle takes up to a few milliseconds.
            if (cursor >= candidates.Count) cursor = 0;
            Unit c = candidates[cursor++];
            if (Pass(c, now)) Deactivate(c, now);
            else NothingToDo(c, now);
        }

        string Precondition(Unit u)
        {
            if (!u.Root.activeInHierarchy) return "switched off by the game";
            if (u.JointBlock != null) return "a joint came back moved";
            if (u.Whole && !(ParkedVehicles.Instance.Live && ParkedVehicles.Instance.IsFrozen(u.Body))) return "not frozen";
            if (Arbiters.Ledger == null || Arbiters.Ledger.Frozen) return "save barrier";
            return null;
        }

        void NothingToDo(Unit u, double now)
        {
            u.NextPass = now + (u.Whole ? WholeRetrySeconds : RetrySeconds);
            u.Why = "kept: " + TopKeeper(u);
            LogOnce(u);
        }

        static string TopKeeper(Unit u)
        {
            string best = "-";
            int n = -1;
            foreach (KeyValuePair<string, int> kv in u.Keepers) if (kv.Value > n) { n = kv.Value; best = kv.Key; }
            return best;
        }

        void CheckDormant(Unit u, float distance, double now)
        {
            if (policy.MustWake(distance, u.Off)) { WakeUnit(u, "player near", false); return; }
            if (u.Whole && !(ParkedVehicles.Instance.Live && ParkedVehicles.Instance.IsFrozen(u.Body))) { WakeUnit(u, "vehicle thawed", true); return; }
            if (u.Virtual) return;
            for (int i = 0; i < u.CoverRoots.Count; i++)
            {
                GameObject go = u.CoverRoots[i];
                if (go == null) continue;
                if (go.activeSelf || !Arbiters.Activation.Has(u.CoverIds[i], Id))
                {
                    WakeUnit(u, "switched by the game", true);
                    policy.Ban(u.Name, now, "the game switched " + go.name);
                    return;
                }
            }
            if (!u.Whole) return;
            if ((u.RootT.position - u.OffPos).sqrMagnitude > 1e-4f || Quaternion.Angle(u.RootT.rotation, u.OffRot) > 0.1f)
            {
                WakeUnit(u, "moved by the game", true);
                return;
            }
            if (u.Stand == null || u.Stand.Holder == null) { WakeUnit(u, "stand-in gone", true); return; }
            for (int i = 0; i < u.FrozenIds.Count; i++)
            {
                if (u.Frozen[i] != null && Arbiters.Physics.Has(u.FrozenIds[i], Id)) continue;
                WakeUnit(u, "the game took a held item", true);
                return;
            }
        }

        /// <summary>A body that moves into a trigger or onto a collider of a part switched off (it would have touched it).</summary>
        void CheckMovers(double now)
        {
            bool any = false;
            for (int i = 0; i < units.Count && !any; i++) any = units[i].Dormant && units[i].Triggers.Count > 0;
            if (!any) return;
            if (now >= nextBodies)
            {
                nextBodies = now + BodiesSeconds;
                bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                lastPos = new Vector3[bodies.Length];
                for (int i = 0; i < bodies.Length; i++) lastPos[i] = bodies[i] != null ? bodies[i].position : Vector3.zero;
                return;
            }
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null) continue;
                Vector3 p = rb.position;
                Vector3 last = lastPos[i];
                lastPos[i] = p;
                if ((p - last).sqrMagnitude < 0.0025f) continue;
                for (int k = 0; k < units.Count; k++)
                {
                    Unit u = units[k];
                    if (!u.Dormant || u.Triggers.Count == 0 || !u.TriggerBox.Contains(p)) continue;
                    for (int t = 0; t < u.Triggers.Count; t++)
                    {
                        if (!u.Triggers[t].Contains(p)) continue;
                        if (!rb.transform.IsChildOf(u.RootT)) WakeUnit(u, "body moving near: " + rb.name, true);
                        break;
                    }
                }
            }
        }

        // ----- The pass: nodes, keepers, cover ------------------------------------------------------------------

        static Transform[] nT = new Transform[4096];
        static int[] nParent = new int[4096], nSize = new int[4096], nContent = new int[4096], nSum = new int[4096];
        static bool[] nOn = new bool[4096], nKeep = new bool[4096], nHas = new bool[4096], nCover = new bool[4096];
        static int nCount;
        static readonly Stack<KeyValuePair<Transform, int>> stack = new Stack<KeyValuePair<Transform, int>>(256);
        static readonly List<KeyValuePair<int, PlayMakerFSM>> passFsms = new List<KeyValuePair<int, PlayMakerFSM>>(1024);
        static readonly List<KeyValuePair<int, AudioSource>> passAudio = new List<KeyValuePair<int, AudioSource>>(256);
        static readonly List<KeyValuePair<int, AudioSource>> passReplay = new List<KeyValuePair<int, AudioSource>>(64);
        static readonly List<KeyValuePair<int, ParticleSystem>> passParticles = new List<KeyValuePair<int, ParticleSystem>>(64);
        static readonly List<KeyValuePair<int, Collider>> passTriggers = new List<KeyValuePair<int, Collider>>(256);
        static readonly Dictionary<string, int> samplesPerKind = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<PlayMakerFSM, string> fsmStatic = new Dictionary<PlayMakerFSM, string>(4096, new RefComparer<PlayMakerFSM>());
        static readonly Dictionary<Type, string> typeKinds = new Dictionary<Type, string>();
        static readonly Dictionary<Type, string> scriptKinds = new Dictionary<Type, string>();

        static void Grow()
        {
            int n = nT.Length * 2;
            Array.Resize(ref nT, n);
            Array.Resize(ref nParent, n);
            Array.Resize(ref nSize, n);
            Array.Resize(ref nContent, n);
            Array.Resize(ref nSum, n);
            Array.Resize(ref nOn, n);
            Array.Resize(ref nKeep, n);
            Array.Resize(ref nHas, n);
            Array.Resize(ref nCover, n);
        }

        /// <summary>Computes the cover of <paramref name="u"/> now. True when there is something to switch off.</summary>
        bool Pass(Unit u, double now)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool whole = u.Whole;
            passFsms.Clear();
            passAudio.Clear();
            passReplay.Clear();
            passParticles.Clear();
            passTriggers.Clear();
            samplesPerKind.Clear();
            u.Keepers.Clear();
            u.Busy.Clear();
            u.Samples.Clear();
            u.Renderers.Clear();
            u.Projectors.Clear();
            u.Solids.Clear();
            u.Joints.Clear();
            u.BusyFsms = 0;
            Vector3 anchor = u.Anchor;
            float wake = u.Off * policy.WakeFactor;
            float half2 = u.Off * u.Off / 4f;
            Camera cam = Camera.main;
            float ppm = cam != null ? Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) : 1000f;

            // Pre-order walk: every subtree is a contiguous range [i, i + size).
            nCount = 0;
            stack.Clear();
            stack.Push(new KeyValuePair<Transform, int>(u.RootT, -1));
            while (stack.Count > 0)
            {
                KeyValuePair<Transform, int> kv = stack.Pop();
                Transform t = kv.Key;
                if (t == null) continue;
                if (nCount >= nT.Length) Grow();
                int i = nCount++, p = kv.Value;
                nT[i] = t;
                nParent[i] = p;
                nSize[i] = 1;
                nContent[i] = 0;
                nKeep[i] = nHas[i] = nCover[i] = false;
                bool on = (p < 0 || nOn[p]) && t.gameObject.activeSelf;
                nOn[i] = on;
                if (on) Classify(u, i, t, anchor, wake, ppm, half2, now, whole);
                for (int c = t.childCount - 1; c >= 0; c--) stack.Push(new KeyValuePair<Transform, int>(t.GetChild(c), i));
            }
            TriggersInUse(u);
            if (whole && nCount > 0) ForeignTriggers(u);

            for (int i = 0; i < nCount; i++) { nSum[i] = nContent[i]; nHas[i] = nKeep[i]; }
            for (int i = nCount - 1; i > 0; i--)
            {
                int p = nParent[i];
                nSize[p] += nSize[i];
                nSum[p] += nSum[i];
                if (nHas[i]) nHas[p] = true;
            }

            u.CoverRoots.Clear();
            u.CoverIds.Clear();
            u.PauseFsms.Clear();
            u.PauseIds.Clear();
            u.Objects.Clear();
            u.Names.Clear();
            u.Tags.Clear();
            u.AudioGuard.Clear();
            u.Replay.Clear();
            u.ParticleGuard.Clear();
            u.Triggers.Clear();
            u.CoverContent = 0;
            // A whole vehicle or nothing: it is the root or no cover at all.
            for (int i = 0; i < nCount && !(whole && nHas[0]);)
            {
                if (nHas[i]) { i++; continue; }      // a keeper inside: look one level deeper
                if (nOn[i] && nSum[i] > 0)
                {
                    GameObject go = nT[i].gameObject;
                    u.CoverRoots.Add(go);
                    u.CoverIds.Add(Arbiters.Id(go));
                    u.CoverContent += nSum[i];
                    for (int j = i; j < i + nSize[i]; j++) nCover[j] = true;
                }
                i += nSize[i];
            }
            u.CoverNodes = 0;
            for (int j = 0; j < nCount; j++)
            {
                if (!nCover[j]) continue;
                u.CoverNodes++;
                GameObject go = nT[j].gameObject;
                u.Objects.Add(go);
                u.Names.Add(go.name);
                string tag = go.tag;
                if (tag != "Untagged") u.Tags.Add(tag);
            }
            for (int k = 0; k < passFsms.Count; k++)
            {
                int j = passFsms[k].Key;
                if (!nCover[j] || !nOn[j]) continue;
                u.PauseFsms.Add(passFsms[k].Value);
                u.PauseIds.Add(Arbiters.Id(passFsms[k].Value));
            }
            for (int k = 0; k < passAudio.Count; k++) if (nCover[passAudio[k].Key]) u.AudioGuard.Add(passAudio[k].Value);
            for (int k = 0; k < passReplay.Count; k++) if (nCover[passReplay[k].Key]) u.Replay.Add(passReplay[k].Value);
            for (int k = 0; k < passParticles.Count; k++) if (nCover[passParticles[k].Key]) u.ParticleGuard.Add(passParticles[k].Value);
            bool first = true;
            for (int k = 0; k < passTriggers.Count; k++)
            {
                if (!nCover[passTriggers[k].Key] || passTriggers[k].Value == null) continue;
                AddWatched(u, passTriggers[k].Value.bounds, ref first);
            }
            if (whole) for (int k = 0; k < u.Solids.Count; k++) if (u.Solids[k] != null) AddWatched(u, u.Solids[k].bounds, ref first);
            u.Nodes = nCount;
            u.Content = nCount > 0 ? nSum[0] : 0;
            u.Fsms = passFsms.Count;
            u.PassAt = now;
            for (int i = 0; i < nCount; i++) nT[i] = null;
            u.PassMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return u.CoverRoots.Count > 0;
        }

        static void AddWatched(Unit u, Bounds b, ref bool first)
        {
            b.Expand(2 * TriggerMargin);
            u.Triggers.Add(b);
            if (first) { u.TriggerBox = b; first = false; }
            else u.TriggerBox.Encapsulate(b);
        }

        void Keep(Unit u, int i, string kind, string detail)
        {
            nKeep[i] = true;
            u.Keepers[kind] = (u.Keepers.TryGetValue(kind, out int n) ? n : 0) + 1;
            // A few paths per kind for the log, except the kinds every place is full of.
            if (u.Logged || kind == "collider" || kind == "visible" || u.Samples.Count >= 24) return;
            samplesPerKind.TryGetValue(kind, out int s);
            if (s >= 2) return;
            samplesPerKind[kind] = s + 1;
            u.Samples.Add(kind + (detail != null ? " (" + detail + ")" : "") + ": " + PathOf(nT[i]));
        }

        void Classify(Unit u, int i, Transform t, Vector3 anchor, float wake, float ppm, float half2, double now, bool whole)
        {
            Component[] comps = t.gameObject.GetComponents<Component>();
            for (int k = 0; k < comps.Length; k++)
            {
                Component c = comps[k];
                if (c == null || c is Transform) continue;
                PlayMakerFSM pm = c as PlayMakerFSM;
                if (pm != null)
                {
                    nContent[i]++;
                    passFsms.Add(new KeyValuePair<int, PlayMakerFSM>(i, pm));
                    string detail;
                    string why = FsmKeep(u, pm, now, whole, out detail);
                    if (why != null) Keep(u, i, why, detail);
                    continue;
                }
                Renderer r = c as Renderer;
                if (r != null)
                {
                    if (!r.enabled) continue;
                    nContent[i]++;
                    if (whole)
                    {
                        // Drawn by the stand-in while the vehicle is off; unless the game reads its visibility.
                        if (RenderScan.IsObserved(r)) Keep(u, i, "visibility read", r.name);
                        else u.Renderers.Add(r);
                        continue;
                    }
                    Bounds b = r.bounds;
                    float d = Mathf.Max(1f, wake - (b.center - anchor).magnitude);
                    if (b.extents.magnitude * 2f * ppm / d >= PixelLimit) Keep(u, i, "visible", null);
                    continue;
                }
                Collider col = c as Collider;
                if (col != null)
                {
                    if (!col.enabled) continue;
                    nContent[i]++;
                    if (col is CharacterController) Keep(u, i, "character controller", null);
                    else if (whole)
                    {
                        if (col.isTrigger) passTriggers.Add(new KeyValuePair<int, Collider>(i, col));
                        else u.Solids.Add(col);
                    }
                    else if (!col.isTrigger) Keep(u, i, "collider", null);
                    else if (col.attachedRigidbody != null) Keep(u, i, "trigger with a body", null);
                    else passTriggers.Add(new KeyValuePair<int, Collider>(i, col));
                    continue;
                }
                Rigidbody rb = c as Rigidbody;
                if (rb != null)
                {
                    nContent[i]++;
                    if (!rb.isKinematic && !(whole && rb.IsSleeping())) Keep(u, i, whole ? "body awake" : "body", null);
                    continue;
                }
                Joint joint = c as Joint;
                if (joint != null)
                {
                    string why = whole ? JointKeep(u, joint) : "joint";
                    if (why != null) Keep(u, i, why, null);
                    continue;
                }
                AudioSource a = c as AudioSource;
                if (a != null)
                {
                    if (!a.enabled) continue;
                    nContent[i]++;
                    if (a.isPlaying)
                    {
                        bool restarts = a.loop && a.playOnAwake;
                        if (whole && a.volume <= SilentVolume) { if (!restarts) passReplay.Add(new KeyValuePair<int, AudioSource>(i, a)); }
                        else if (whole || !restarts) Keep(u, i, "sound playing", a.clip != null ? a.clip.name : null);
                    }
                    else if (a.playOnAwake && a.clip != null) passAudio.Add(new KeyValuePair<int, AudioSource>(i, a));
                    continue;
                }
                Animation an = c as Animation;
                if (an != null)
                {
                    if (!an.enabled) continue;
                    nContent[i]++;
                    if (an.isPlaying) Keep(u, i, "animation playing", null);
                    continue;
                }
                ParticleSystem ps = c as ParticleSystem;
                if (ps != null)
                {
                    nContent[i]++;
                    if (ps.isPlaying) Keep(u, i, "particles", null);
                    else if (ps.playOnAwake) passParticles.Add(new KeyValuePair<int, ParticleSystem>(i, ps));
                    continue;
                }
                Light l = c as Light;
                if (l != null)
                {
                    if (!l.enabled) continue;
                    nContent[i]++;
                    Keep(u, i, "light", null);
                    continue;
                }
                Behaviour be = c as Behaviour;
                if (be != null && !be.enabled) continue;
                if (whole)
                {
                    Projector pr = c as Projector;
                    if (pr != null) { nContent[i]++; u.Projectors.Add(pr); continue; }
                    // Legacy particles (the boat's trail): nothing to lose while the emitter is off and empty.
                    ParticleEmitter pe = c as ParticleEmitter;
                    if (pe != null)
                    {
                        nContent[i]++;
                        // "emit" stays on for the boat's trail; it makes particles only with an emission above 0.
                        if (pe.particleCount > 0 || (pe.emit && pe.maxEmission > 0f)) Keep(u, i, "particles (legacy)", null);
                        continue;
                    }
                    if (c is ParticleAnimator) continue;
                }
                string kind = KindOf(c.GetType());
                if (kind == null) continue;
                nContent[i]++;
                if (kind.Length == 0) continue;
                if (whole)
                {
                    // A vehicle off whole: the PlayMaker proxies only forward to its FSMs (paused, checked above);
                    // a script is fine when it does nothing on OnEnable / OnDisable and runs no coroutines.
                    if (kind.StartsWith("PlayMaker proxy", StringComparison.Ordinal)) continue;
                    if (kind.StartsWith("script ", StringComparison.Ordinal)) kind = ScriptKeep(c);
                    if (kind == null) continue;
                }
                Keep(u, i, kind, null);
            }
            if (!whole && nContent[i] > 0 && (t.position - anchor).sqrMagnitude > half2) Keep(u, i, "far from the root", null);
        }

        /// <summary>Null: passive, nothing to gain or lose; "": passive but counted; else the keeper kind.</summary>
        static string KindOf(Type t)
        {
            string k;
            if (typeKinds.TryGetValue(t, out k)) return k;
            string n = t.Name;
            if (t == typeof(MeshFilter) || t == typeof(LODGroup) || t == typeof(SleepSentinel) || n == "TextMesh" || n == "OcclusionArea" || n == "OcclusionPortal") k = null;
            else if (n == "AudioReverbZone" || (n.StartsWith("Audio", StringComparison.Ordinal) && n.EndsWith("Filter", StringComparison.Ordinal))) k = "";
            else if (typeof(Animator).IsAssignableFrom(t)) k = "animator";
            else if (typeof(Camera).IsAssignableFrom(t) || n == "GUILayer" || n == "FlareLayer") k = "camera";
            else if (n.StartsWith("PlayMaker", StringComparison.Ordinal) || t.Namespace == "HutongGames.PlayMaker") k = "PlayMaker proxy " + n;
            else if (t.Assembly == typeof(Component).Assembly) k = "Unity " + n;
            else k = "script " + n;
            typeKinds[t] = k;
            return k;
        }

        /// <summary>
        /// A script of a vehicle switched off whole: null when it is safe (its fields stay, only its Update pauses), else
        /// the keeper kind. Unsafe: it does something in OnEnable / OnDisable, or has coroutines (they die with the
        /// object and do not come back). Known exceptions from the decompiled game: windshield (rain texture made again
        /// on OnEnable), the UnityCar driving aids (Modifier: OnDisable resets their per-step values), EventSounds
        /// (unsafe only when it plays a sound on enable or disable).
        /// </summary>
        static string ScriptKeep(Component c)
        {
            Type t = c.GetType();
            string k;
            if (!scriptKinds.TryGetValue(t, out k))
            {
                k = null;
                bool known = t.Name == "windshield" || IsModifier(t) || t.Name == "EventSounds";
                if (!known)
                {
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    for (Type b = t; b != null && b != typeof(MonoBehaviour) && b != typeof(Behaviour); b = b.BaseType)
                    {
                        foreach (MethodInfo m in b.GetMethods(F))
                        {
                            if (m.Name == "OnEnable" || m.Name == "OnDisable") { k = "script " + t.Name + " (" + m.Name + ")"; break; }
                            if (typeof(IEnumerator).IsAssignableFrom(m.ReturnType)) { k = "script " + t.Name + " (coroutine)"; break; }
                        }
                        if (k != null) break;
                    }
                }
                scriptKinds[t] = k ?? "";
            }
            if (!string.IsNullOrEmpty(k)) return k;
            if (t.Name == "EventSounds" && (Flag(c, "useEnableSound") || Flag(c, "useDisableSound"))) return "script EventSounds (sound on enable)";
            return null;
        }

        static bool IsModifier(Type t)
        {
            for (Type b = t; b != null; b = b.BaseType) if (b.Name == "Modifier") return true;
            return false;
        }

        static bool Flag(Component c, string field)
        {
            FieldInfo f = c.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f != null && f.GetValue(c) is bool b && b;
        }

        /// <summary>
        /// A joint of a vehicle switched off whole is made again by Unity when it comes back. 5.x found hinges coming back
        /// with a new rest angle (HingeManager). Joints inside the vehicle are allowed and measured: each one's angle and
        /// pose against its body before going off and one tick after coming back (JointCheck). A joint to another body
        /// keeps the vehicle on (world.vehicles does not freeze such a vehicle anyway).
        /// </summary>
        static string JointKeep(Unit u, Joint j)
        {
            if (j.connectedBody != null && !j.connectedBody.transform.IsChildOf(u.RootT)) return "joint to another body";
            // Measured (deact-veh, 363 joints made again): hinges, fixed, spring and character joints come back where
            // they were; the boat's ConfigurableJoint (DriverHeadPivot) came back 3 cm lower every time.
            if (!(j is HingeJoint || j is FixedJoint || j is SpringJoint || j is CharacterJoint)) return "joint " + j.GetType().Name;
            u.Joints.Add(j);
            return null;
        }

        /// <summary>
        /// A vehicle standing in a trigger of somebody else (a road lane, an area of a job): when it comes back, Unity
        /// sends OnTriggerEnter again for that pair, which the game never does for a car that stood there all along
        /// (equiv: the Jonnez's and the Kekmet's Steering.Lane 99 -> -2). Such a vehicle stays on.
        /// </summary>
        void ForeignTriggers(Unit u)
        {
            if (u.Solids.Count == 0) return;
            Bounds all = new Bounds();
            bool first = true;
            for (int i = 0; i < u.Solids.Count; i++)
            {
                if (u.Solids[i] == null) continue;
                if (first) { all = u.Solids[i].bounds; first = false; }
                else all.Encapsulate(u.Solids[i].bounds);
            }
            if (first) return;
            Collider[] hits = Physics.OverlapSphere(all.center, all.extents.magnitude + 0.3f);
            for (int h = 0; h < hits.Length; h++)
            {
                Collider o = hits[h];
                if (o == null || !o.isTrigger || !o.enabled || o.transform.IsChildOf(u.RootT)) continue;
                Bounds ob = o.bounds;
                for (int i = 0; i < u.Solids.Count; i++)
                {
                    if (u.Solids[i] == null || !u.Solids[i].bounds.Intersects(ob)) continue;
                    Keep(u, 0, "in a trigger", PathOf(o.transform));
                    return;
                }
            }
        }

        static void RelativePose(Joint j, out Vector3 pos, out Quaternion rot)
        {
            Transform a = j.transform;
            Rigidbody other = j.connectedBody;
            if (other == null) { pos = a.position; rot = a.rotation; return; }
            Quaternion inv = Quaternion.Inverse(other.rotation);
            pos = inv * (a.position - other.position);
            rot = inv * a.rotation;
        }

        long jointChecks, jointMoved, hingeChanged;
        float maxHingeDelta, maxJointMove;
        readonly List<string> jointNotes = new List<string>();

        void RecordJoints(Unit u)
        {
            u.JointRecords.Clear();
            for (int i = 0; i < u.Joints.Count; i++)
            {
                Joint j = u.Joints[i];
                if (j == null) continue;
                JointRecord r = new JointRecord { Joint = j, Angle = j is HingeJoint ? ((HingeJoint)j).angle : 0f };
                RelativePose(j, out r.Pos, out r.Rot);
                u.JointRecords.Add(r);
            }
        }

        /// <summary>One tick after the vehicle came back: did any joint come back other than it went?</summary>
        void JointCheck(Unit u)
        {
            u.JointCheckAt = -1;
            for (int i = 0; i < u.JointRecords.Count; i++)
            {
                JointRecord r = u.JointRecords[i];
                if (r.Joint == null) continue;
                jointChecks++;
                Vector3 pos;
                Quaternion rot;
                RelativePose(r.Joint, out pos, out rot);
                float move = (pos - r.Pos).magnitude, turn = Quaternion.Angle(rot, r.Rot);
                HingeJoint h = r.Joint as HingeJoint;
                float delta = h != null ? Mathf.Abs(Mathf.DeltaAngle(h.angle, r.Angle)) : 0f;
                if (delta > maxHingeDelta) maxHingeDelta = delta;
                if (move > maxJointMove) maxJointMove = move;
                bool moved = move > 0.005f || turn > 0.5f, changed = delta > 0.5f;
                if (moved) jointMoved++;
                if (changed) hingeChanged++;
                if (!moved && !changed) continue;
                string note = PathOf(r.Joint.transform) + " " + r.Joint.GetType().Name + (h != null ? " angle " + r.Angle.ToString("0.00", Inv) + " -> " + h.angle.ToString("0.00", Inv) : "") +
                              ", moved " + (move * 1000f).ToString("0.0", Inv) + " mm / " + turn.ToString("0.00", Inv) + " deg";
                if (jointNotes.Count < 12) jointNotes.Add(note);
                // Unity made the joint again somewhere else: a second time could move it further. This vehicle stays on.
                if (u.JointBlock == null)
                {
                    u.JointBlock = note;
                    ModLog.Line(Id + ": " + u.Name + " stays on from now on: a joint came back moved (" + note + ")");
                }
            }
            u.JointRecords.Clear();
        }

        /// <summary>Why this FSM must stay on, null when it may go off with its object.</summary>
        string FsmKeep(Unit u, PlayMakerFSM pm, double now, bool whole, out string detail)
        {
            detail = pm.FsmName;
            string why;
            if (!fsmStatic.TryGetValue(pm, out why))
            {
                Fsm f0 = pm.Fsm;
                why = f0 == null ? "FSM without data" : !Vanilla.For(pm).Value ? "FSM not vanilla" : FsmSleep.ProxyReason(f0);
                fsmStatic[pm] = why ?? "";
            }
            // Switched off whole: the proxies of triggers, collisions and FixedUpdate go off with their FSM (paused,
            // checked below); sub-FSMs run inside their owner, which the watch does not see.
            if (!string.IsNullOrEmpty(why) && !(whole && why != "FSM not vanilla" && why != "FSM with sub-FSMs")) return why;
            Fsm f = pm.Fsm;
            if (FsmSleep.IsAsleep(f)) return null;      // asleep: idle by proof
            if (!pm.enabled) return null;               // switched off by the game: it does not run
            if (!f.Started) return "FSM not started";
            FsmState st;
            if (FsmSleep.IsIdle(f, out st))
            {
                if (FsmSleep.HasMessageTransitions(f, st)) return "FSM waits for a Unity message";
                if (DormantIndex.EnteredWithin(f, now, QuietSeconds)) return "FSM active lately";
                return null;
            }
            CountBusy(u, f);
            u.BusyFsms++;
            return whole ? StillBusy(u, f, now) : "FSM busy";
        }

        /// <summary>
        /// A busy FSM of a vehicle: null when it may be paused - nothing timed, nothing taking events, and for 10 s the same
        /// state, the same variables (except the outputs of Get* actions) and no state entry.
        /// </summary>
        string StillBusy(Unit u, Fsm f, double now)
        {
            FsmState active, switchTo;
            FsmAccess.ReadFsm(f, out active, out switchTo);
            if (active == null || switchTo != null) return "FSM switching";
            if (f.DelayedEvents.Count > 0) return "FSM delayed event";
            Fsm owner;
            List<FsmStateAction> actions;
            FsmAccess.ReadState(active, out owner, out actions);
            bool waiting = false;
            string outside = null;
            if (actions != null)
                for (int i = 0; i < actions.Count; i++)
                {
                    FsmStateAction a = actions[i];
                    if (a == null) continue;
                    Type t = a.GetType();
                    int k = ActionKind(t);
                    if (k == ActionWait) { waiting = true; continue; }
                    if (k == ActionTimed) return "FSM timer " + t.Name;
                    if (k == ActionEvents) return "FSM action takes events " + t.Name;
                    if (outside == null && FollowsControls(a, t)) outside = t.Name;
                }
            // A state that follows the player's controls (the vehicle's car controller reads cInput every frame, or keys,
            // buttons, axes) and can leave on them: in the game it reacts while the vehicle is far (equiv-20261101-004117:
            // the Gifu's ActivateBrake went to "Brake ON" and emptied its air brakes when the player braked the Satsuma).
            // A state without transitions only maps the controls onto the vehicle and takes them again on its first
            // frame back (the Kekmet's brake lever, Steering.Lane = PlayerRoadLane; expected.json). The distance to the
            // player cannot change a far vehicle: it comes back before the player is nearer than 0.9 x 125 m.
            if (outside != null && active.Transitions != null && active.Transitions.Length > 0) return "FSM follows the controls " + outside;
            if (FsmSleep.HasMessageTransitions(f, active)) return "FSM waits for a Unity message";
            int hash = Hash(f, active, u.RootT);
            Watch w;
            bool entered = u.Watches.TryGetValue(f, out w) && DormantIndex.EnteredWithin(f, now, now - w.Since + 0.001);
            if (w == null || w.Hash != hash || (!waiting && (entered || !ReferenceEquals(w.State, active))))
            {
                u.Watches[f] = new Watch { State = active, Hash = hash, Since = now };
                return waiting ? "FSM loop watched" : "FSM watched";
            }
            if (now - w.Since < QuietSeconds) return waiting ? "FSM loop watched" : "FSM watched";
            if (!waiting) return null;
            // A loop that waits (the fuel tank: Wait -> recompute the mass -> Wait): it went round at least once with every
            // variable the same, and it is in its waiting state now. Paused, its timer stops where it is and goes on when
            // the vehicle is back: the loop runs later by the time the vehicle was off, with the same values.
            if (entered) w.Cycled = true;
            return w.Cycled ? null : "FSM loop watched";
        }

        const int ActionFine = 0, ActionTimed = 1, ActionEvents = 2, ActionWait = 3;
        static readonly Dictionary<Type, int> actionKinds = new Dictionary<Type, int>();
        static readonly string[] TimedNames = { "time", "duration", "delay", "timer", "seconds" };

        /// <summary>
        /// Wait / RandomWait: a timer whose progress is the state time (allowed in a loop that changes nothing). Timed:
        /// any other action with a time field (interpolations, tweens) - its progress is invisible in the variables.
        /// Takes events: an action that handles events itself (FsmStateAction.Event). Accumulators like FloatAdd per
        /// second are fine: their progress is in the variables, the watch sees it.
        /// </summary>
        static int ActionKind(Type t)
        {
            int k;
            if (actionKinds.TryGetValue(t, out k)) return k;
            k = ActionFine;
            MethodInfo ev = t.GetMethod("Event", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(FsmEvent) }, null);
            if (ev != null && ev.DeclaringType != typeof(FsmStateAction)) k = ActionEvents;
            else if (t.Name == "Wait" || t.Name == "RandomWait") k = ActionWait;
            else if (t.Name.IndexOf("Wait", StringComparison.Ordinal) >= 0 || t.Name.IndexOf("Interpolate", StringComparison.Ordinal) >= 0 ||
                     t.Name.StartsWith("iTween", StringComparison.Ordinal) || t.Name.StartsWith("Ease", StringComparison.Ordinal)) k = ActionTimed;
            else
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (fi.FieldType == typeof(FsmFloat) && Array.IndexOf(TimedNames, fi.Name.ToLowerInvariant()) >= 0) { k = ActionTimed; break; }
            actionKinds[t] = k;
            return k;
        }

        // The variables written by the Get* actions of a state that read the world outside the vehicle (the player's
        // distance, positions of other objects, the clock): they follow the world, not the FSM. A Get* action that reads
        // only the vehicle itself (its speed, a property of its own parts) stays in the hash: what it reads must stand
        // still too (equiv: the Kekmet's brake lever and the Gifu's air brake read their own parts and were still moving).
        readonly Dictionary<FsmState, HashSet<string>> excludedVars = new Dictionary<FsmState, HashSet<string>>(new RefComparer<FsmState>());
        static readonly Dictionary<Type, FieldInfo[]> variableFields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, FieldInfo[]> targetFields = new Dictionary<Type, FieldInfo[]>();

        HashSet<string> Excluded(FsmState st, Fsm f, Transform root)
        {
            HashSet<string> set;
            if (excludedVars.TryGetValue(st, out set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            FsmStateAction[] actions = st.Actions;
            if (actions != null)
                foreach (FsmStateAction a in actions)
                {
                    if (a == null) continue;
                    Type t = a.GetType();
                    if (t.Namespace != "HutongGames.PlayMaker.Actions" || !t.Name.StartsWith("Get", StringComparison.Ordinal)) continue;
                    if (!ReadsOutside(a, t, f, root)) continue;
                    FieldInfo[] fields;
                    if (!variableFields.TryGetValue(t, out fields))
                    {
                        List<FieldInfo> list = new List<FieldInfo>();
                        foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (typeof(NamedVariable).IsAssignableFrom(fi.FieldType)) list.Add(fi);
                        variableFields[t] = fields = list.ToArray();
                    }
                    foreach (FieldInfo fi in fields)
                    {
                        NamedVariable nv = fi.GetValue(a) as NamedVariable;
                        if (nv != null && nv.UseVariable && !string.IsNullOrEmpty(nv.Name)) set.Add(nv.Name);
                    }
                }
            excludedVars[st] = set;
            return set;
        }

        static readonly Dictionary<Type, FieldInfo[]> propertyFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>
        /// The action reads the player's controls: a key, button or axis, or a property of a car controller that runs
        /// (an enabled AxisCarController copies cInput into its fields every frame, wherever the player is). Writing to a
        /// controller, or reading a disabled one, follows nothing.
        /// </summary>
        static bool FollowsControls(FsmStateAction a, Type t)
        {
            string n = t.Name;
            if (!n.StartsWith("Get", StringComparison.Ordinal)) return false;
            if (n.IndexOf("Key", StringComparison.Ordinal) >= 0 || n.IndexOf("Button", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("Axis", StringComparison.Ordinal) >= 0) return true;
            FieldInfo[] fields;
            if (!propertyFields.TryGetValue(t, out fields))
            {
                List<FieldInfo> list = new List<FieldInfo>();
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (fi.FieldType == typeof(FsmProperty)) list.Add(fi);
                propertyFields[t] = fields = list.ToArray();
            }
            foreach (FieldInfo fi in fields)
            {
                FsmProperty p = fi.GetValue(a) as FsmProperty;
                Behaviour b = p != null && p.TargetObject != null ? p.TargetObject.Value as Behaviour : null;
                if (b != null && b.GetType().Name.EndsWith("CarController", StringComparison.Ordinal) && b.enabled && b.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        /// <summary>
        /// The action reads something that is not part of the vehicle: one of its targets (owner, game object, property
        /// target) lies outside the root, a property of a car controller (the player's controls: AxisCarController reads
        /// cInput every frame), or it has no target at all (time, input, the camera).
        /// </summary>
        static bool ReadsOutside(FsmStateAction a, Type t, Fsm f, Transform root)
        {
            FieldInfo[] fields;
            if (!targetFields.TryGetValue(t, out fields))
            {
                List<FieldInfo> list = new List<FieldInfo>();
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (fi.FieldType == typeof(FsmOwnerDefault) || fi.FieldType == typeof(FsmGameObject) || fi.FieldType == typeof(FsmProperty)) list.Add(fi);
                targetFields[t] = fields = list.ToArray();
            }
            bool any = false;
            foreach (FieldInfo fi in fields)
            {
                object v = fi.GetValue(a);
                GameObject go = null;
                bool resolved = false;
                if (v is FsmOwnerDefault od) { go = f.GetOwnerDefaultTarget(od); resolved = true; }
                else if (v is FsmGameObject g)
                {
                    // An output variable of the action is not a target (GetChild's storeResult and the like).
                    if (g.UseVariable && !string.IsNullOrEmpty(g.Name) && fi.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase)) continue;
                    go = g.Value;
                    resolved = true;
                }
                else if (v is FsmProperty p && p.TargetObject != null)
                {
                    UnityEngine.Object o = p.TargetObject.Value;
                    if (o != null && o.GetType().Name.EndsWith("CarController", StringComparison.Ordinal)) return true;
                    go = o is GameObject og ? og : o is Component c ? c.gameObject : null;
                    resolved = true;
                }
                if (!resolved) continue;
                any = true;
                if (go == null || !go.transform.IsChildOf(root)) return true;
            }
            return !any;
        }

        int Hash(Fsm f, FsmState st, Transform root)
        {
            HashSet<string> ex = Excluded(st, f, root);
            FsmVariables v = f.Variables;
            int h = 17;
            if (v == null) return h;
            foreach (FsmFloat x in v.FloatVariables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmInt x in v.IntVariables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value;
            foreach (FsmBool x in v.BoolVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value ? 1 : 2);
            foreach (FsmString x in v.StringVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value != null ? x.Value.GetHashCode() : 0);
            foreach (FsmVector2 x in v.Vector2Variables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmVector3 x in v.Vector3Variables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmRect x in v.RectVariables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmQuaternion x in v.QuaternionVariables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmColor x in v.ColorVariables) if (!ex.Contains(x.Name)) h = h * 31 + x.Value.GetHashCode();
            foreach (FsmGameObject x in v.GameObjectVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value != null ? x.Value.GetInstanceID() : 0);
            foreach (FsmObject x in v.ObjectVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value != null ? x.Value.GetInstanceID() : 0);
            foreach (FsmMaterial x in v.MaterialVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value != null ? x.Value.GetInstanceID() : 0);
            foreach (FsmTexture x in v.TextureVariables) if (!ex.Contains(x.Name)) h = h * 31 + (x.Value != null ? x.Value.GetInstanceID() : 0);
            return h;
        }

        static void CountBusy(Unit u, Fsm f)
        {
            FsmState active, switchTo;
            FsmAccess.ReadFsm(f, out active, out switchTo);
            List<FsmStateAction> actions = null;
            if (active != null)
            {
                Fsm owner;
                FsmAccess.ReadState(active, out owner, out actions);
            }
            if (actions == null || actions.Count == 0)
            {
                string k = active == null ? "(no state)" : f.DelayedEvents.Count > 0 ? "(delayed event)" : "(state not finished)";
                u.Busy[k] = (u.Busy.TryGetValue(k, out int m) ? m : 0) + 1;
                return;
            }
            for (int i = 0; i < actions.Count; i++)
            {
                string k = actions[i] != null ? actions[i].GetType().Name : "?";
                u.Busy[k] = (u.Busy.TryGetValue(k, out int n) ? n : 0) + 1;
            }
        }

        /// <summary>A trigger of the part touched by a body of somebody else: it would see it enter again.</summary>
        void TriggersInUse(Unit u)
        {
            for (int k = 0; k < passTriggers.Count; k++)
            {
                int i = passTriggers[k].Key;
                Collider col = passTriggers[k].Value;
                if (nKeep[i] || col == null) continue;
                Bounds b = col.bounds;
                Collider[] hits = Physics.OverlapSphere(b.center, Mathf.Max(0.1f, b.extents.magnitude));
                for (int h = 0; h < hits.Length; h++)
                {
                    Collider o = hits[h];
                    if (o == null || ReferenceEquals(o, col) || o.attachedRigidbody == null) continue;
                    if (o.transform.IsChildOf(u.RootT) || !b.Intersects(o.bounds)) continue;
                    Keep(u, i, "trigger in use", o.attachedRigidbody.name);
                    break;
                }
            }
        }

        /// <summary>
        /// Foreign bodies (not kinematic) touching a solid collider of the vehicle: items in it, things on it - held
        /// while it is off. Null, or why not: a heavy body (another vehicle) is not stopped by this system.
        /// </summary>
        string FindTouching(Unit u)
        {
            u.Frozen.Clear();
            if (u.Solids.Count == 0) return null;
            Bounds all = new Bounds();
            bool first = true;
            for (int i = 0; i < u.Solids.Count; i++)
            {
                if (u.Solids[i] == null) continue;
                if (first) { all = u.Solids[i].bounds; first = false; }
                else all.Encapsulate(u.Solids[i].bounds);
            }
            if (first) return null;
            Collider[] hits = Physics.OverlapSphere(all.center, all.extents.magnitude + 0.3f);
            for (int h = 0; h < hits.Length; h++)
            {
                Collider o = hits[h];
                Rigidbody rb = o != null ? o.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || u.Frozen.Contains(rb) || rb.transform.IsChildOf(u.RootT)) continue;
                Bounds ob = o.bounds;
                ob.Expand(0.2f);
                for (int i = 0; i < u.Solids.Count; i++)
                {
                    if (u.Solids[i] == null || !u.Solids[i].bounds.Intersects(ob)) continue;
                    if (rb.mass >= 300f) return "touching " + rb.name;
                    u.Frozen.Add(rb);
                    break;
                }
            }
            return null;
        }

        static string PathOf(Transform t)
        {
            if (t == null) return "?";
            StringBuilder sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        // ----- Off and on ---------------------------------------------------------------------------------------

        void Deactivate(Unit u, double now)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (!verify)
            {
                if (u.Whole)
                {
                    // The still copy first (nothing shown yet), then the foreign bodies on it held.
                    string err;
                    u.Stand = StandIn.Build(u.RootT, u.Renderers, u.Projectors, Id, out err);
                    if (u.Stand == null) { Refused(u, now, "stand-in: " + err); return; }
                    u.StandText = u.Stand.Describe();
                    u.FrozenIds.Clear();
                    string touching = FindTouching(u);
                    if (touching != null) { Refused(u, now, touching); return; }
                    for (int i = 0; i < u.Frozen.Count; i++)
                    {
                        int id = Arbiters.Id(u.Frozen[i]);
                        u.FrozenIds.Add(id);
                        SetResult r = Arbiters.Physics.Add(id, Id);
                        if (r != SetResult.Ok) { Refused(u, now, "item " + u.Frozen[i].name + " " + r); return; }
                    }
                }
                if (u.Whole) RecordJoints(u);
                // RestartGuard: every FSM paused first, so OnDisable keeps its state.
                for (int i = 0; i < u.PauseIds.Count; i++)
                {
                    SetResult r = Arbiters.FsmPause.Add(u.PauseIds[i], Id);
                    if (r != SetResult.Ok) { Refused(u, now, "pause " + r); return; }
                }
                for (int i = 0; i < u.CoverIds.Count; i++)
                {
                    SetResult r = Arbiters.Activation.Add(u.CoverIds[i], Id);
                    if (r != SetResult.Ok) { Refused(u, now, "switch " + r); return; }
                }
                if (u.Stand != null) u.Stand.Show(true);
                u.OffPos = u.RootT.position;
                u.OffRot = u.RootT.rotation;
            }
            BuildIndex(u);
            DormantIndex.Register(u);
            u.Dormant = true;
            u.Virtual = verify;
            u.DormantSince = now;
            u.Offs++;
            offs++;
            u.OffMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (u.OffMs > u.MaxOffMs) u.MaxOffMs = u.OffMs;
            u.Why = "-";
            LogOnce(u);
            if (logTransitions)
                ModLog.Line(Id + ": " + u.Name + (verify ? " virtually off" : " off") + " (" + u.CoverRoots.Count + " roots, " + u.PauseFsms.Count +
                            " FSMs (" + u.BusyFsms + " busy), " + u.Frozen.Count + " items held, pass " + Ms(u.PassMs) + ", off " + Ms(u.OffMs) +
                            " ms; stand-in " + u.StandText + ")");
        }

        /// <summary>The ledger refused something halfway: everything of the part back, and no new try for a while.</summary>
        void Refused(Unit u, double now, string why)
        {
            GiveBack(u);
            u.Why = "refused: " + why;
            u.NextPass = now + RetrySeconds;
            policy.Ban(u.Name, now, why);
            ModLog.Line(Id + ": " + u.Name + " not switched off: " + why);
        }

        void GiveBack(Unit u)
        {
            Arbiters.Restoring++;
            try { for (int i = 0; i < u.CoverIds.Count; i++) Arbiters.Activation.Remove(u.CoverIds[i], Id); }
            finally { Arbiters.Restoring--; }
            // The same frame: the stand-in goes (the ledger destroys it), the held items move again.
            if (u.Stand != null) { u.Stand.Release(); u.Stand = null; }
            for (int i = 0; i < u.FrozenIds.Count; i++) Arbiters.Physics.Remove(u.FrozenIds[i], Id);
            u.FrozenIds.Clear();
            for (int i = 0; i < u.AudioGuard.Count; i++)
            {
                AudioSource a = u.AudioGuard[i];
                if (a != null && a.isPlaying) a.Stop();
            }
            Replay(u);
            for (int i = 0; i < u.ParticleGuard.Count; i++)
            {
                ParticleSystem ps = u.ParticleGuard[i];
                if (ps != null && ps.isPlaying) { ps.Stop(); ps.Clear(); }
            }
            for (int i = 0; i < u.PauseIds.Count; i++) Arbiters.FsmPause.Remove(u.PauseIds[i], Id);
        }

        /// <summary>Silent sounds that played when the vehicle went off (Unity stopped them; they would not start alone).</summary>
        static void Replay(Unit u)
        {
            for (int i = 0; i < u.Replay.Count; i++)
            {
                AudioSource a = u.Replay[i];
                if (a != null && a.enabled && a.gameObject.activeInHierarchy && !a.isPlaying) a.Play();
            }
        }

        /// <summary>The events every FSM of the cover reacts to, as they are now that the part is off.</summary>
        static void BuildIndex(Unit u)
        {
            u.IndexFsms.Clear();
            u.Events.Clear();
            List<FsmEvent> list = new List<FsmEvent>(8);
            for (int i = 0; i < u.PauseFsms.Count; i++)
            {
                PlayMakerFSM pm = u.PauseFsms[i];
                Fsm f = pm != null ? pm.Fsm : null;
                if (f == null || (!pm.enabled && !FsmSleep.IsAsleep(f))) continue;
                list.Clear();
                AddEvents(list, f.GlobalTransitions);
                FsmState st = f.ActiveState;
                if (st != null) AddEvents(list, st.Transitions);
                FsmEvent[] events = list.ToArray();
                u.IndexFsms.Add(new KeyValuePair<Fsm, FsmEvent[]>(f, events));
                for (int e = 0; e < events.Length; e++) u.Events.Add(events[e]);
            }
        }

        static void AddEvents(List<FsmEvent> list, FsmTransition[] transitions)
        {
            if (transitions == null) return;
            for (int i = 0; i < transitions.Length; i++)
            {
                FsmEvent e = transitions[i] != null ? transitions[i].FsmEvent : null;
                if (e != null && !list.Contains(e)) list.Add(e);
            }
        }

        /// <summary>From DormantIndex: something of the game reaches into the part.</summary>
        internal void WakeFromHook(Unit u, string why) { WakeUnit(u, why, true); }

        void WakeUnit(Unit u, string why, bool byEvent, bool counted = true)
        {
            if (!u.Dormant) return;
            u.Dormant = false;
            DormantIndex.Unregister(u);
            double now = ModLog.Now();
            long t0 = Stopwatch.GetTimestamp();
            if (!u.Virtual)
            {
                GiveBack(u);
                if (u.JointRecords.Count > 0) u.JointCheckAt = now + 0.2;   // after a physics step
            }
            u.OnMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (u.OnMs > u.MaxOnMs) u.MaxOnMs = u.OnMs;
            u.Ons++;
            ons++;
            u.DormantTotal += now - u.DormantSince;
            u.Why = "woken: " + why;
            string key = why.Length > 60 ? why.Substring(0, 60) : why;
            Count(u.WakeReasons, key);
            Count(wakeReasons, key);
            if (!counted) return;
            if (policy.Woken(u.Name, now, byEvent, why)) ModLog.Line(Id + ": " + u.Name + " banned for " + policy.BanLength(u.Name) + " s (" + why + ")");
            if (logTransitions) ModLog.Line(Id + ": " + u.Name + " on (" + why + ", " + Ms(u.OnMs) + " ms, off " + Ms(now - u.DormantSince) + " s)");
        }

        static void Count(Dictionary<string, long> d, string key)
        {
            if (!d.ContainsKey(key) && d.Count >= 40) key = "(other)";
            d[key] = (d.TryGetValue(key, out long n) ? n : 0) + 1;
        }

        void WakeAll(string why, bool counted)
        {
            for (int i = 0; i < units.Count; i++)
            {
                try { WakeUnit(units[i], why, false, counted); }
                catch (Exception ex) { ModLog.Report(Id + "/WakeAll", ex, units[i].Name); }
            }
        }

        /// <summary>The ledger gave everything back (RestoreAll): the parts are on, the stand-ins gone, the arbiters forgot.</summary>
        void ForgetRestored(double now)
        {
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (!u.Dormant || u.Virtual) continue;
                u.Dormant = false;
                DormantIndex.Unregister(u);
                u.Stand = null;
                u.FrozenIds.Clear();
                u.DormantTotal += now - u.DormantSince;
                u.Ons++;
                ons++;
                u.Why = "woken: ledger restore";
                Count(u.WakeReasons, "ledger restore");
                Count(wakeReasons, "ledger restore");
                policy.Woken(u.Name, now, false);
                for (int k = 0; k < u.AudioGuard.Count; k++) if (u.AudioGuard[k] != null && u.AudioGuard[k].isPlaying) u.AudioGuard[k].Stop();
                Replay(u);
            }
        }

        void LogOnce(Unit u)
        {
            if (u.Logged) return;
            u.Logged = true;
            StringBuilder sb = new StringBuilder();
            sb.Append(Id).Append(": ").Append(u.Name).Append(" first pass: ");
            AppendCover(sb, u);
            if (u.Samples.Count > 0) sb.Append("\n  samples: ").Append(string.Join("\n  ", u.Samples.ToArray()));
            ModLog.Line(sb.ToString());
        }

        // ----- A/B (TestBot) -----------------------------------------------------------------------------------

        /// <summary>Off = every part on and nothing switched off; on = every part that may go off now, at once.</summary>
        public string SetAb(bool on)
        {
            if (player == null) return "no player yet";
            logTransitions = false;
            try
            {
                if (!on)
                {
                    abHeld = true;
                    WakeAll("A/B", false);
                    return null;
                }
                abHeld = false;
                double now = ModLog.Now();
                Vector3 pp = player.position;
                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (u.Dormant || u.Root == null || (u.Anchor - pp).magnitude < u.Off || Precondition(u) != null) continue;
                    // The cover of a recent pass, if its FSMs are still the same; else a new pass.
                    if (now - u.PassAt > ReuseCoverSeconds || !StillQuiet(u, now)) { if (!Pass(u, now)) continue; }
                    if (u.CoverRoots.Count > 0) Deactivate(u, now);
                }
                return null;
            }
            finally { logTransitions = true; }
        }

        /// <summary>This system holds <paramref name="root"/> switched off now (world.vehicles keeps it frozen meanwhile).</summary>
        public bool IsOff(GameObject root)
        {
            if (!Live || ReferenceEquals(root, null)) return false;
            for (int i = 0; i < units.Count; i++)
                if (ReferenceEquals(units[i].Root, root)) return units[i].Dormant && !units[i].Virtual;
            return false;
        }

        float testDistance;

        /// <summary>Test only ("mopr world distance"): every vehicle switches off from <paramref name="m"/> m; 0 = the defaults.</summary>
        internal string SetTestDistance(float m)
        {
            testDistance = m > 0 ? m : 0;
            foreach (Unit u in units) u.Off = testDistance > 0 ? testDistance : u.DefaultOff;
            return Id + ": switch-off distance " + (testDistance > 0 ? testDistance.ToString("0.#", Inv) + " m (test)" : "back to the defaults");
        }

        bool StillQuiet(Unit u, double now)
        {
            for (int i = 0; i < u.CoverRoots.Count; i++) if (u.CoverRoots[i] == null) return false;
            for (int i = 0; i < u.PauseFsms.Count; i++)
            {
                if (u.PauseFsms[i] == null) return false;
                string detail;
                if (FsmKeep(u, u.PauseFsms[i], now, u.Whole, out detail) != null) return false;
            }
            return true;
        }

        // ----- Report -------------------------------------------------------------------------------------------

        static string Ms(double v) => v.ToString(v < 10 ? "0.00" : "0.0", Inv);

        static void AppendCover(StringBuilder sb, Unit u)
        {
            sb.Append(u.Whole ? (u.CoverRoots.Count > 0 ? "whole" : "kept") : "cover " + u.CoverRoots.Count + " roots")
              .Append(", objects ").Append(u.CoverNodes).Append('/').Append(u.Nodes)
              .Append(", FSMs ").Append(u.PauseFsms.Count).Append('/').Append(u.Fsms).Append(" (busy ").Append(u.BusyFsms).Append(")")
              .Append(", content ").Append(u.CoverContent).Append('/').Append(u.Content).Append("; keepers ");
            AppendTop(sb, u.Keepers.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), 8);
            if (u.Busy.Count > 0)
            {
                sb.Append("; busy FSMs run ");
                AppendTop(sb, u.Busy.Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value)), 6);
            }
            if (u.Whole) sb.Append("; renderers ").Append(u.Renderers.Count).Append(", silent sounds ").Append(u.Replay.Count);
            sb.Append("; pass ").Append(Ms(u.PassMs)).Append(" ms");
        }

        static void AppendTop(StringBuilder sb, IEnumerable<KeyValuePair<string, long>> items, int max)
        {
            int n = 0;
            foreach (KeyValuePair<string, long> kv in items.OrderByDescending(x => x.Value))
            {
                if (n++ >= max) { sb.Append(", ..."); break; }
                if (n > 1) sb.Append(", ");
                sb.Append(kv.Key).Append(' ').Append(kv.Value);
            }
            if (n == 0) sb.Append('-');
        }

        public override void Report(StringBuilder sb)
        {
            int off = 0;
            foreach (Unit u in units) if (u.Dormant) off++;
            sb.Append("  ").Append(units.Count).Append(" vehicles, ").Append(off).Append(verify ? " virtually off" : " off").Append(" now; off ").Append(offs)
              .Append(" times, on ").Append(ons).Append(" times; bans ").Append(policy.Bans).Append("; wakes: ");
            AppendTop(sb, wakeReasons, 10);
            sb.Append("\n  aimed at a vehicle: ").Append(DormantIndex.TargetHits).Append(" (reading ").Append(DormantIndex.TargetHarmless).Append("); mismatches ")
              .Append(DormantIndex.Mismatches);
            foreach (KeyValuePair<string, long> kv in DormantIndex.MismatchKinds) sb.Append("; ").Append(kv.Key).Append(' ').Append(kv.Value);
            if (gone > 0) sb.Append("; roots gone ").Append(gone);
            sb.Append("\n  joints checked after coming back: ").Append(jointChecks).Append(", moved ").Append(jointMoved).Append(", hinge angle changed ")
              .Append(hingeChanged).Append(" (max ").Append(maxHingeDelta.ToString("0.00", Inv)).Append(" deg, max move ")
              .Append((maxJointMove * 1000f).ToString("0.0", Inv)).Append(" mm)");
            if (jointNotes.Count > 0) sb.Append(": ").Append(string.Join("; ", jointNotes.ToArray()));
            sb.Append('\n');
            double now = ModLog.Now();
            foreach (Unit u in units)
            {
                double total = u.DormantTotal + (u.Dormant ? now - u.DormantSince : 0);
                sb.Append("    ").Append(u.Name).Append(' ').Append(u.Off.ToString("0", Inv)).Append(" m: ")
                  .Append(u.Dormant ? (u.Virtual ? "virtually off " : "off ") + (now - u.DormantSince).ToString("0", Inv) + " s" : "on (" + u.Why + ")")
                  .Append(", off in all ").Append(total.ToString("0", Inv)).Append(" s");
                if (u.PassAt > -1e8)
                {
                    sb.Append(" | ");
                    AppendCover(sb, u);
                }
                if (u.Offs > 0)
                    sb.Append("; off ").Append(u.Offs).Append(" x ").Append(Ms(u.OffMs)).Append(" ms (max ").Append(Ms(u.MaxOffMs)).Append("), on ").Append(u.Ons)
                      .Append(" x ").Append(Ms(u.OnMs)).Append(" ms (max ").Append(Ms(u.MaxOnMs)).Append("); stand-in ").Append(u.StandText)
                      .Append("; items held ").Append(u.Frozen.Count);
                if (u.WakeReasons.Count > 0)
                {
                    sb.Append("; woken by ");
                    AppendTop(sb, u.WakeReasons, 5);
                }
                sb.Append('\n');
            }
            if (skipped.Count > 0 || missing.Count > 0)
            {
                sb.Append("  skipped: ").Append(string.Join("; ", skipped.ToArray()));
                if (missing.Count > 0) sb.Append(skipped.Count > 0 ? "; " : "").Append("not found yet: ").Append(string.Join(", ", missing.Select(d => d.Path).ToArray()));
                sb.Append('\n');
            }
        }
    }
}
