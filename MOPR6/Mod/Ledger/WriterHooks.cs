// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Known writers of the game (requirement 5.4, design §3 mechanism 2, task 20): the PlayMaker actions that write the
// properties the ledger holds get a prefix that tells the ledger BEFORE the game writes. The entry becomes External,
// the arbiter drops its reasons and releases it without writing anything, and the game's value stays. This covers
// what the restore check cannot see: the game writing the same value we hold (it hides a renderer we hid - without
// the hook the restore would show it again).
// The list comes from the TestBot writer scan of vanilla (MOPR6\Docs\ledger.ru.md): which actions exist in the
// game, how often, and for SetProperty which members it writes.
//   ActivateGameObject   GoActive (recursive: the whole subtree), OnEnter / OnUpdate (everyFrame) / OnExit (reset)
//   Blink                RendererEnabled (rendererOnly) or GoActive, OnEnter / OnUpdate
//   EnableBehaviour      BehaviourEnabled (ColliderEnabled is not a Behaviour), OnEnter / OnExit (reset)
//   EnableFSM            FsmEnabled, OnEnter / OnExit (reset)
//   SetIsKinematic       RbKinematic                 SetLayer   Layer
//   SetParent            Parent                      WakeUp     RbSleep
//   SetProperty          "enabled" of a Renderer / Collider / Light / Behaviour, Rigidbody.isKinematic /
//                        useGravity / detectCollisions, Light.shadows; OnEnter / OnUpdate (everyFrame)
// Every prefix returns at once while the ledger is empty, so the cost is one static read per action call.
// Scripts of the game (FuelTank, CarDynamics...) are found by the background drift check (mechanism 3).

using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MOPR6.Core.Ledger;
using MOPR6.Diagnostics;
using UnityEngine;

namespace MOPR6.Ledger
{
    public static class WriterHooks
    {
        const string HarmonyId = "mopr.ledger.writers";
        static HarmonyInstance harmony;
        public static int Patched { get; private set; }
        public static readonly List<string> Missing = new List<string>();
        /// <summary>Notes given to the ledger (held entries that a game action was about to write).</summary>
        public static long Notes;

        public static bool Installed => harmony != null;

        public static string Install()
        {
            if (harmony != null) return null;
            HarmonyInstance h = HarmonyInstance.Create(HarmonyId);
            Patch(h, typeof(ActivateGameObject), "OnEnter", nameof(Activate));
            Patch(h, typeof(ActivateGameObject), "OnUpdate", nameof(Activate));
            Patch(h, typeof(ActivateGameObject), "OnExit", nameof(ActivateExit));
            Patch(h, typeof(Blink), "OnEnter", nameof(BlinkWrite));
            Patch(h, typeof(Blink), "OnUpdate", nameof(BlinkWrite));
            Patch(h, typeof(EnableBehaviour), "OnEnter", nameof(EnableBehaviourEnter));
            Patch(h, typeof(EnableBehaviour), "OnExit", nameof(EnableBehaviourExit));
            Patch(h, typeof(EnableFSM), "OnEnter", nameof(EnableFsmEnter));
            Patch(h, typeof(EnableFSM), "OnExit", nameof(EnableFsmExit));
            Patch(h, typeof(SetIsKinematic), "OnEnter", nameof(Kinematic));
            Patch(h, typeof(SetLayer), "OnEnter", nameof(Layer));
            Patch(h, typeof(SetParent), "OnEnter", nameof(Parent));
            Patch(h, typeof(WakeUp), "OnEnter", nameof(Wake));
            Patch(h, typeof(SetProperty), "OnEnter", nameof(Property));
            Patch(h, typeof(SetProperty), "OnUpdate", nameof(Property));
            harmony = h;
            ModLog.Line("ledger writer hooks: " + Patched + " patched" + (Missing.Count > 0 ? ", not found: " + string.Join(", ", Missing.ToArray()) : ""));
            return null;
        }

        static void Patch(HarmonyInstance h, Type t, string method, string prefix)
        {
            MethodInfo target = t.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (target == null) { Missing.Add(t.Name + "." + method); return; }
            h.Patch(target, new HarmonyMethod(typeof(WriterHooks).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            Patched++;
        }

        static bool Idle => Arbiters.Ledger == null || Arbiters.Ledger.Count == 0;

        static void Note(UnityEngine.Object target, PropKind kind, PropValue value, string source)
        {
            if (target == null) return;
            ChangeLedger ledger = Arbiters.Ledger;
            PropKey key = new PropKey(target.GetInstanceID(), kind);
            if (ledger.Find(key) == null) return;
            Notes++;
            ledger.NoteExternalWrite(key, value, source);
        }

        /// <summary>A recursive activation: every held GoActive under <paramref name="root"/> (the ledger is small).</summary>
        static void NoteSubtree(GameObject root, PropValue value, string source)
        {
            Note(root, PropKind.GoActive, value, source);
            List<PropKey> under = null;
            foreach (LedgerEntry e in Arbiters.Ledger.Entries)
            {
                if (e.Key.Kind != PropKind.GoActive || e.Key.Target == root.GetInstanceID()) continue;
                GameObject g = Arbiters.Access.Get<GameObject>(e.Key.Target);
                if (g != null && g.transform.IsChildOf(root.transform)) (under ?? (under = new List<PropKey>())).Add(e.Key);
            }
            if (under == null) return;
            foreach (PropKey k in under) { Notes++; Arbiters.Ledger.NoteExternalWrite(k, value, source); }
        }

        static string Where(FsmStateAction a) => a != null && a.Fsm != null ? (a.Fsm.Name + "/" + (a.State != null ? a.State.Name : "?")) : null;

        // ----- Prefixes -------------------------------------------------------------------------------------

        static void Activate(ActivateGameObject __instance)
        {
            if (Idle) return;
            try
            {
                GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go == null || __instance.activate == null) return;
                PropValue v = PropValue.Bool(__instance.activate.Value);
                if (__instance.recursive != null && __instance.recursive.Value) NoteSubtree(go, v, "ActivateGameObject");
                else Note(go, PropKind.GoActive, v, "ActivateGameObject");
            }
            catch (Exception ex) { ModLog.Report("ledger/ActivateGameObject", ex, Where(__instance)); }
        }

        static void ActivateExit(ActivateGameObject __instance)
        {
            if (Idle || !__instance.resetOnExit) return;
            try
            {
                // OnExit resets the object activated last (a private field; the same target as OnEnter unless it changed).
                GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go == null || __instance.activate == null) return;
                PropValue v = PropValue.Bool(!__instance.activate.Value);
                if (__instance.recursive != null && __instance.recursive.Value) NoteSubtree(go, v, "ActivateGameObject (exit)");
                else Note(go, PropKind.GoActive, v, "ActivateGameObject (exit)");
            }
            catch (Exception ex) { ModLog.Report("ledger/ActivateGameObject.exit", ex, Where(__instance)); }
        }

        static void BlinkWrite(Blink __instance)
        {
            if (Idle) return;
            try
            {
                GameObject go = __instance.gameObject.OwnerOption != OwnerDefaultOption.UseOwner ? __instance.gameObject.GameObject.Value : __instance.Owner;
                if (go == null) return;
                // Blink toggles on its own timer: any held value of its target is the game's from now on.
                if (__instance.rendererOnly) Note(go.GetComponent<Renderer>(), PropKind.RendererEnabled, PropValue.Bool(true), "Blink");
                else Note(go, PropKind.GoActive, PropValue.Bool(true), "Blink");
            }
            catch (Exception ex) { ModLog.Report("ledger/Blink", ex, Where(__instance)); }
        }

        static Behaviour BehaviourOf(EnableBehaviour a)
        {
            if (a.component != null) return a.component as Behaviour;
            GameObject go = a.Fsm.GetOwnerDefaultTarget(a.gameObject);
            return go != null && a.behaviour != null ? go.GetComponent(a.behaviour.Value) as Behaviour : null;
        }

        static void EnableBehaviourEnter(EnableBehaviour __instance)
        {
            if (Idle) return;
            try
            {
                if (__instance.enable == null) return;
                Behaviour b = BehaviourOf(__instance);
                Note(b, b is PlayMakerFSM ? PropKind.FsmEnabled : PropKind.BehaviourEnabled, PropValue.Bool(__instance.enable.Value), "EnableBehaviour");
            }
            catch (Exception ex) { ModLog.Report("ledger/EnableBehaviour", ex, Where(__instance)); }
        }

        static void EnableBehaviourExit(EnableBehaviour __instance)
        {
            if (Idle || __instance.resetOnExit == null || !__instance.resetOnExit.Value || __instance.enable == null) return;
            try
            {
                Behaviour b = BehaviourOf(__instance);
                Note(b, b is PlayMakerFSM ? PropKind.FsmEnabled : PropKind.BehaviourEnabled, PropValue.Bool(!__instance.enable.Value), "EnableBehaviour (exit)");
            }
            catch (Exception ex) { ModLog.Report("ledger/EnableBehaviour.exit", ex, Where(__instance)); }
        }

        static PlayMakerFSM FsmOf(EnableFSM a)
        {
            GameObject go = a.gameObject.OwnerOption != OwnerDefaultOption.UseOwner ? a.gameObject.GameObject.Value : a.Owner;
            if (go == null) return null;
            string name = a.fsmName != null ? a.fsmName.Value : null;
            if (string.IsNullOrEmpty(name)) return go.GetComponent<PlayMakerFSM>();
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>()) if (f.FsmName == name) return f;
            return null;
        }

        static void EnableFsmEnter(EnableFSM __instance)
        {
            if (Idle || __instance.enable == null) return;
            try { Note(FsmOf(__instance), PropKind.FsmEnabled, PropValue.Bool(__instance.enable.Value), "EnableFSM"); }
            catch (Exception ex) { ModLog.Report("ledger/EnableFSM", ex, Where(__instance)); }
        }

        static void EnableFsmExit(EnableFSM __instance)
        {
            if (Idle || __instance.resetOnExit == null || !__instance.resetOnExit.Value || __instance.enable == null) return;
            try { Note(FsmOf(__instance), PropKind.FsmEnabled, PropValue.Bool(!__instance.enable.Value), "EnableFSM (exit)"); }
            catch (Exception ex) { ModLog.Report("ledger/EnableFSM.exit", ex, Where(__instance)); }
        }

        static void Kinematic(SetIsKinematic __instance)
        {
            if (Idle || __instance.isKinematic == null) return;
            try
            {
                GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go != null) Note(go.GetComponent<Rigidbody>(), PropKind.RbKinematic, PropValue.Bool(__instance.isKinematic.Value), "SetIsKinematic");
            }
            catch (Exception ex) { ModLog.Report("ledger/SetIsKinematic", ex, Where(__instance)); }
        }

        static void Layer(SetLayer __instance)
        {
            if (Idle) return;
            try { Note(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject), PropKind.Layer, PropValue.Int(__instance.layer), "SetLayer"); }
            catch (Exception ex) { ModLog.Report("ledger/SetLayer", ex, Where(__instance)); }
        }

        static void Parent(SetParent __instance)
        {
            if (Idle) return;
            try
            {
                GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go == null) return;
                GameObject p = __instance.parent != null ? __instance.parent.Value : null;
                Note(go.transform, PropKind.Parent, PropValue.Ref(p != null ? p.transform.GetInstanceID() : 0), "SetParent");
            }
            catch (Exception ex) { ModLog.Report("ledger/SetParent", ex, Where(__instance)); }
        }

        static void Wake(WakeUp __instance)
        {
            if (Idle) return;
            try
            {
                GameObject go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go != null) Note(go.GetComponent<Rigidbody>(), PropKind.RbSleep, PropValue.Bool(false), "WakeUp");
            }
            catch (Exception ex) { ModLog.Report("ledger/WakeUp", ex, Where(__instance)); }
        }

        static void Property(SetProperty __instance)
        {
            if (Idle) return;
            try
            {
                FsmProperty p = __instance.targetProperty;
                if (p == null || p.TargetObject == null) return;
                UnityEngine.Object target = p.TargetObject.Value;
                if (target == null) return;
                PropKind kind;
                PropValue value;
                if (!Classify(target, p, out kind, out value)) return;
                Note(target, kind, value, "SetProperty " + target.GetType().Name + "." + p.PropertyName);
            }
            catch (Exception ex) { ModLog.Report("ledger/SetProperty", ex, Where(__instance)); }
        }

        /// <summary>The ledger kind a SetProperty writes, or false (a member the ledger does not hold).</summary>
        static bool Classify(UnityEngine.Object target, FsmProperty p, out PropKind kind, out PropValue value)
        {
            kind = PropKind.GoActive;
            value = default(PropValue);
            string name = p.PropertyName;
            if (string.IsNullOrEmpty(name) || name.IndexOf('.') >= 0) return false;
            bool b = p.BoolParameter != null && p.BoolParameter.Value;
            switch (name)
            {
                case "enabled":
                    if (target is Renderer) kind = PropKind.RendererEnabled;
                    else if (target is Collider) kind = PropKind.ColliderEnabled;
                    else if (target is Light) kind = PropKind.LightEnabled;
                    else if (target is PlayMakerFSM) kind = PropKind.FsmEnabled;
                    else if (target is Behaviour) kind = PropKind.BehaviourEnabled;
                    else return false;
                    value = PropValue.Bool(b);
                    return true;
                case "isKinematic": kind = PropKind.RbKinematic; value = PropValue.Bool(b); return target is Rigidbody;
                case "useGravity": kind = PropKind.RbUseGravity; value = PropValue.Bool(b); return target is Rigidbody;
                case "detectCollisions": kind = PropKind.RbDetectCollisions; value = PropValue.Bool(b); return target is Rigidbody;
                case "shadows":
                    kind = PropKind.LightShadows;
                    // An enum member: FsmProperty writes it from StringParameter (Enum.Parse); the value is only a label here.
                    value = PropValue.Int(-1);
                    return target is Light;
                case "shadowCastingMode":
                    kind = PropKind.ShadowCasting;
                    value = PropValue.Int(-1);
                    return target is Renderer;
                default:
                    return false;
            }
        }
    }
}
