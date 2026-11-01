// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The ledger's only way to the game (IPropertyAccess, design §3): get / set of every PropKind on Unity objects.
// Unity 5 has no "object by instance id" at runtime, so every object the mod touches is registered first
// (Register); a destroyed object reads as gone.
// CameraLayerCull is an array of 32 distances per camera (PropValue.Floats).

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Ledger;
using UnityEngine;

namespace MOPR6.Ledger
{
    public sealed class UnityAccess : IPropertyAccess
    {
        readonly Dictionary<int, UnityEngine.Object> objects = new Dictionary<int, UnityEngine.Object>();
        readonly HashSet<int> created = new HashSet<int>();
        public const int QualityTarget = 0;   // QualityShadowDistance has no object

        /// <summary>Makes an object addressable by its instance id. Returns the id.</summary>
        public int Register(UnityEngine.Object o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            int id = o.GetInstanceID();
            objects[id] = o;
            return id;
        }

        public T Get<T>(int id) where T : UnityEngine.Object
        {
            UnityEngine.Object o;
            return objects.TryGetValue(id, out o) && o != null ? o as T : null;
        }

        /// <summary>Forgets destroyed objects (scene change).</summary>
        public void Clear()
        {
            objects.Clear();
            created.Clear();
        }

        public bool TryGet(PropKey key, out PropValue value)
        {
            value = default(PropValue);
            if (key.Kind == PropKind.QualityShadowDistance)
            {
                value = PropValue.Float(QualitySettings.shadowDistance);
                return true;
            }
            if (key.Kind == PropKind.CreatedObject)
            {
                if (Get<UnityEngine.Object>(key.Target) == null && !created.Contains(key.Target)) return false;
                value = PropValue.Bool(created.Contains(key.Target));
                return true;
            }
            UnityEngine.Object o = Get<UnityEngine.Object>(key.Target);
            if (o == null) return false;
            switch (key.Kind)
            {
                case PropKind.GoActive: return Is(o as GameObject, g => PropValue.Bool(g.activeSelf), out value);
                case PropKind.Layer: return Is(o as GameObject, g => PropValue.Int(g.layer), out value);
                case PropKind.BehaviourEnabled:
                case PropKind.FsmEnabled: return Is(o as Behaviour, b => PropValue.Bool(b.enabled), out value);
                case PropKind.RendererEnabled: return Is(o as Renderer, r => PropValue.Bool(r.enabled), out value);
                case PropKind.ShadowCasting: return Is(o as Renderer, r => PropValue.Int((int)r.shadowCastingMode), out value);
                case PropKind.ColliderEnabled: return Is(o as Collider, c => PropValue.Bool(c.enabled), out value);
                case PropKind.RbKinematic: return Is(o as Rigidbody, r => PropValue.Bool(r.isKinematic), out value);
                case PropKind.RbDetectCollisions: return Is(o as Rigidbody, r => PropValue.Bool(r.detectCollisions), out value);
                case PropKind.RbUseGravity: return Is(o as Rigidbody, r => PropValue.Bool(r.useGravity), out value);
                case PropKind.RbSleep: return Is(o as Rigidbody, r => PropValue.Bool(r.IsSleeping()), out value);
                case PropKind.FsmRestartOnEnable:
                    return Is(o as PlayMakerFSM, p => PropValue.Bool(p.Fsm != null && p.Fsm.RestartOnEnable), out value);
                case PropKind.Parent:
                    return Is(o as Transform, t => PropValue.Ref(t.parent != null ? Register(t.parent) : 0), out value);
                case PropKind.LocalPosition:
                    return Is(o as Transform, t => { Vector3 p = t.localPosition; return PropValue.Vector(p.x, p.y, p.z); }, out value);
                case PropKind.LocalRotation:
                    return Is(o as Transform, t => { Quaternion q = t.localRotation; return PropValue.Vector(q.x, q.y, q.z, q.w); }, out value);
                case PropKind.LightEnabled: return Is(o as Light, l => PropValue.Bool(l.enabled), out value);
                case PropKind.LightShadows: return Is(o as Light, l => PropValue.Int((int)l.shadows), out value);
                case PropKind.CameraLayerCull: return Is(o as Camera, c => PropValue.Floats(c.layerCullDistances), out value);
                case PropKind.CameraCullSpherical: return Is(o as Camera, c => PropValue.Bool(c.layerCullSpherical), out value);
                default: return false;
            }
        }

        public bool TrySet(PropKey key, PropValue v)
        {
            if (key.Kind == PropKind.QualityShadowDistance)
            {
                QualitySettings.shadowDistance = v.X;
                return true;
            }
            if (key.Kind == PropKind.CreatedObject)
            {
                if (v.AsBool) { created.Add(key.Target); return true; }
                created.Remove(key.Target);
                UnityEngine.Object made = Get<UnityEngine.Object>(key.Target);
                if (made != null) UnityEngine.Object.Destroy(made is Component c ? c.gameObject : made);
                return true;
            }
            UnityEngine.Object o = Get<UnityEngine.Object>(key.Target);
            if (o == null) return false;
            switch (key.Kind)
            {
                case PropKind.GoActive: return Do(o as GameObject, g => g.SetActive(v.AsBool));
                case PropKind.Layer: return Do(o as GameObject, g => g.layer = v.I);
                case PropKind.BehaviourEnabled:
                case PropKind.FsmEnabled: return Do(o as Behaviour, b => b.enabled = v.AsBool);
                case PropKind.RendererEnabled: return Do(o as Renderer, r => r.enabled = v.AsBool);
                case PropKind.ShadowCasting: return Do(o as Renderer, r => r.shadowCastingMode = (UnityEngine.Rendering.ShadowCastingMode)v.I);
                case PropKind.ColliderEnabled: return Do(o as Collider, c => c.enabled = v.AsBool);
                case PropKind.RbKinematic: return Do(o as Rigidbody, r => r.isKinematic = v.AsBool);
                case PropKind.RbDetectCollisions: return Do(o as Rigidbody, r => r.detectCollisions = v.AsBool);
                case PropKind.RbUseGravity: return Do(o as Rigidbody, r => r.useGravity = v.AsBool);
                case PropKind.RbSleep: return Do(o as Rigidbody, r => { if (v.AsBool) r.Sleep(); else r.WakeUp(); });
                case PropKind.FsmRestartOnEnable:
                    return Do(o as PlayMakerFSM, p => { if (p.Fsm != null) p.Fsm.RestartOnEnable = v.AsBool; });
                case PropKind.Parent:
                    return Do(o as Transform, t => t.parent = v.I == 0 ? null : Get<Transform>(v.I));
                case PropKind.LocalPosition: return Do(o as Transform, t => t.localPosition = new Vector3(v.X, v.Y, v.Z));
                case PropKind.LocalRotation: return Do(o as Transform, t => t.localRotation = new Quaternion(v.X, v.Y, v.Z, v.W));
                case PropKind.LightEnabled: return Do(o as Light, l => l.enabled = v.AsBool);
                case PropKind.LightShadows: return Do(o as Light, l => l.shadows = (LightShadows)v.I);
                case PropKind.CameraLayerCull:
                    // Unity wants exactly 32 distances; it copies the array.
                    if (v.A == null || v.A.Length != 32) return false;
                    return Do(o as Camera, c => c.layerCullDistances = v.A);
                case PropKind.CameraCullSpherical: return Do(o as Camera, c => c.layerCullSpherical = v.AsBool);
                default: return false;
            }
        }

        static bool Is<T>(T o, Func<T, PropValue> read, out PropValue value) where T : UnityEngine.Object
        {
            value = default(PropValue);
            if (o == null) return false;
            value = read(o);
            return true;
        }

        static bool Do<T>(T o, Action<T> write) where T : UnityEngine.Object
        {
            if (o == null) return false;
            write(o);
            return true;
        }

        public int Depth(int target)
        {
            Transform t = TransformOf(Get<UnityEngine.Object>(target));
            int d = 0;
            while (t != null && t.parent != null && d < 256) { t = t.parent; d++; }
            return d;
        }

        public string Describe(int target)
        {
            UnityEngine.Object o = Get<UnityEngine.Object>(target);
            if (o == null) return "#" + target + " (gone)";
            Transform t = TransformOf(o);
            if (t == null) return o.name;
            StringBuilder sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            if (o is Component c && !(o is Transform)) sb.Append(':').Append(c.GetType().Name);
            return sb.ToString();
        }

        static Transform TransformOf(UnityEngine.Object o)
        {
            if (o is GameObject g) return g.transform;
            if (o is Component c) return c.transform;
            return null;
        }
    }
}
