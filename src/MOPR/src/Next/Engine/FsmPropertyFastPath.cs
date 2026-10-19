// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Fast path for PlayMaker's FsmProperty (GetProperty / SetProperty actions, ~130 calls per frame in MSC).
//
// FsmProperty.GetValue/SetValue go through System.Reflection every call: PropertyInfo.GetValue/SetValue,
// a boxed value and an argument array per call. The TestBot allocation profiler showed that these two actions
// produce ~80% of all PlayMaker garbage (300-430 KB/s), which directly drives the ~110 ms GC freezes.
//
// The fast path calls the SAME getter/setter through a compiled delegate instead of reflection. It replicates
// FsmProperty 1.6 line by line and only handles the common case; everything else runs the original:
//   * CheckForReinitialize() would call Init()            -> original
//   * targetObjectCached == null || memberInfo == null      -> nothing (as the original)
//   * a single public instance field/property, PropertyType equal to the member type and one of
//     bool/int/float/string/Vector2/Vector3/Rect/Quaternion/Color/GameObject/Material/Texture/Object subclass
//     (for these exact types exactly one branch of the original if/else chain matches)
//   * the target is an instance of the declaring type      (else reflection throws its own exception)
// Property getter/setter exceptions are wrapped in TargetInvocationException, exactly like reflection does.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOP.Next.Engine
{
    static class FsmPropertyFastPath
    {
        const string HarmonyId = "com.anickon.mopr.engine.property";

        enum Kind : byte { Bool, Int, Float, String, Vector2, Vector3, Rect, Quaternion, Color, GameObject, Material, Texture, UnityObject }

        sealed class Accessor
        {
            public Kind Kind;
            public Type DeclaringType;
            public Type ValueType;
            public bool Wrap;             // property: wrap exceptions like reflection does
            public Delegate Getter;       // Func<object, T> (value kinds) or Func<object, object>
            public Delegate Setter;       // Action<object, T> or Action<object, object>; null if not writable
            public Type LastTargetType;   // last target type that passed the instance check
        }

        sealed class Entry { public MemberInfo[] Infos; public Accessor Accessor; }

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<FsmProperty, Entry> entries = new Dictionary<FsmProperty, Entry>(512, new RefComparer<FsmProperty>());
        static readonly Dictionary<MemberInfo, Accessor> accessors = new Dictionary<MemberInfo, Accessor>(new RefComparer<MemberInfo>());
        static readonly Accessor Unsupported = new Accessor();
        static readonly Dictionary<Type, Kind> kinds = new Dictionary<Type, Kind>
        {
            { typeof(bool), Kind.Bool }, { typeof(int), Kind.Int }, { typeof(float), Kind.Float }, { typeof(string), Kind.String },
            { typeof(Vector2), Kind.Vector2 }, { typeof(Vector3), Kind.Vector3 }, { typeof(Rect), Kind.Rect },
            { typeof(Quaternion), Kind.Quaternion }, { typeof(Color), Kind.Color }, { typeof(GameObject), Kind.GameObject },
            { typeof(Material), Kind.Material }, { typeof(Texture), Kind.Texture }
        };

        static HarmonyInstance harmony;
        public static bool Shadow { get; private set; }
        public static string Status { get; private set; } = "not installed";
        public static long GetCalls, GetFast, SetCalls, SetFast, ShadowChecked, ShadowMismatch;
        public static readonly List<string> MismatchLog = new List<string>();

        public static string Install(bool shadow)
        {
            Remove();
            Shadow = shadow;
            try
            {
                MethodInfo get = typeof(FsmProperty).GetMethod("GetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                MethodInfo set = typeof(FsmProperty).GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
                if (get == null || set == null ||
                    typeof(FsmProperty).GetField("initialized", I)?.FieldType != typeof(bool) ||
                    typeof(FsmProperty).GetField("targetObjectCached", I)?.FieldType != typeof(UnityEngine.Object) ||
                    typeof(FsmProperty).GetField("memberInfo", I)?.FieldType != typeof(MemberInfo[]))
                    return Status = "FsmProperty layout changed";

                harmony = HarmonyInstance.Create(HarmonyId);
                if (shadow)
                {
                    harmony.Patch(get, Hook("GetPrefixShadow"), Hook("GetPostfixShadow"), null);
                    harmony.Patch(set, Hook("SetPrefixShadow"), null, null);
                }
                else
                {
                    harmony.Patch(get, Hook("GetPrefix"), null, null);
                    harmony.Patch(set, Hook("SetPrefix"), null, null);
                }
                Status = "patched";
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                return Status = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Remove()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            entries.Clear();
            shadowProperty = null;
            Status = "not installed";
        }

        public static void ResetStats()
        {
            GetCalls = GetFast = SetCalls = SetFast = ShadowChecked = ShadowMismatch = 0;
            MismatchLog.Clear();
            SlowReasons.Clear();
        }

        /// <summary>Drops cached references to FsmProperty objects of unloaded scenes.</summary>
        public static void ClearCaches()
        {
            entries.Clear();
            shadowProperty = null;
        }

        static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(FsmPropertyFastPath).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ----- Accessors ----------------------------------------------------------------------------------

        static Accessor Resolve(FsmProperty property, MemberInfo[] infos)
        {
            if (entries.TryGetValue(property, out Entry e) && ReferenceEquals(e.Infos, infos))
                return e.Accessor;

            Accessor acc = Unsupported;
            if (infos.Length == 1 && infos[0] != null)
            {
                MemberInfo m = infos[0];
                if (!accessors.TryGetValue(m, out acc))
                {
                    try { acc = Build(m) ?? Unsupported; }
                    catch { acc = Unsupported; }
                    accessors[m] = acc;
                }
            }
            if (entries.Count > 20000) entries.Clear();
            entries[property] = new Entry { Infos = infos, Accessor = acc };
            return acc;
        }

        static Accessor Build(MemberInfo m)
        {
            Type declaring = m.DeclaringType;
            if (declaring == null || declaring.IsValueType || declaring.IsGenericTypeDefinition) return null;

            Type valueType;
            MethodInfo getter = null, setter = null;
            FieldInfo field = null;
            if (m is PropertyInfo p)
            {
                if (p.GetIndexParameters().Length != 0) return null;
                getter = p.GetGetMethod(false);
                setter = p.GetSetMethod(false);
                if (getter == null || getter.IsStatic) return null;
                if (setter != null && setter.IsStatic) setter = null;
                valueType = p.PropertyType;
            }
            else if (m is FieldInfo f)
            {
                if (f.IsStatic || !f.IsPublic) return null;
                field = f;
                valueType = f.FieldType;
            }
            else return null;

            Kind kind;
            if (!kinds.TryGetValue(valueType, out kind))
            {
                // Original: "PropertyType.IsSubclassOf(typeof(UnityEngine.Object))" branch (GameObject/Material/Texture are matched before).
                if (!valueType.IsSubclassOf(typeof(UnityEngine.Object))) return null;
                kind = Kind.UnityObject;
            }

            bool reference = !valueType.IsValueType;
            Type delegateValue = reference ? typeof(object) : valueType;

            // Getter: (object target) => ((Declaring)target).Member
            DynamicMethod dg = new DynamicMethod("MOPR_PropGet", delegateValue, new[] { typeof(object) }, typeof(FsmPropertyFastPath), true);
            ILGenerator il = dg.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaring);
            if (field != null) il.Emit(OpCodes.Ldfld, field);
            else il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
            il.Emit(OpCodes.Ret);
            Delegate get = dg.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(object), delegateValue));

            // Setter: (object target, T value) => ((Declaring)target).Member = value
            Delegate set = null;
            bool writable = field != null ? !field.IsInitOnly && !field.IsLiteral : setter != null;
            if (writable)
            {
                DynamicMethod ds = new DynamicMethod("MOPR_PropSet", typeof(void), new[] { typeof(object), delegateValue }, typeof(FsmPropertyFastPath), true);
                il = ds.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, declaring);
                il.Emit(OpCodes.Ldarg_1);
                if (reference) il.Emit(OpCodes.Castclass, valueType);
                if (field != null) il.Emit(OpCodes.Stfld, field);
                else il.Emit(setter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, setter);
                il.Emit(OpCodes.Ret);
                set = ds.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(object), delegateValue));
            }

            return new Accessor { Kind = kind, DeclaringType = declaring, ValueType = valueType, Wrap = field == null, Getter = get, Setter = set };
        }

        // ----- Shared checks ------------------------------------------------------------------------------

        /// <summary>
        /// Returns the accessor if the fast path applies, else null (run the original). <paramref name="nothing"/> is set
        /// when the original would do nothing at all (target or member missing).
        /// </summary>
        static Accessor Check(FsmProperty property, UnityEngine.Object cached, MemberInfo[] infos, bool initialized, out bool nothing)
        {
            nothing = false;
            // FsmProperty.CheckForReinitialize(): Init() would run -> leave it to PlayMaker.
            FsmObject targetObject = property.TargetObject;
            if (!initialized || targetObject == null || cached != targetObject.Value ||
                (targetObject.UseVariable && !ReferenceEquals(property.TargetType, targetObject.ObjectType)))
                return null;

            if (cached == null || infos == null) { nothing = true; return null; }

            Accessor acc = Resolve(property, infos);
            if (acc == Unsupported || !ReferenceEquals(property.PropertyType, acc.ValueType)) return null;

            Type targetType = cached.GetType();
            if (!ReferenceEquals(targetType, acc.LastTargetType))
            {
                if (!acc.DeclaringType.IsAssignableFrom(targetType)) return null;
                acc.LastTargetType = targetType;
            }
            return acc;
        }

        static T Get<T>(Accessor a, object target)
        {
            Func<object, T> f = (Func<object, T>)a.Getter;
            if (!a.Wrap) return f(target);
            try { return f(target); }
            catch (Exception ex) { throw new TargetInvocationException(ex); }
        }

        static void Set<T>(Accessor a, object target, T value)
        {
            Action<object, T> f = (Action<object, T>)a.Setter;
            if (!a.Wrap) { f(target, value); return; }
            try { f(target, value); }
            catch (Exception ex) { throw new TargetInvocationException(ex); }
        }

        /// <summary>FsmProperty.GetValue() for the supported kinds (same branch, same parameter, same setter).</summary>
        static void DoGet(FsmProperty p, Accessor a, object target)
        {
            switch (a.Kind)
            {
                case Kind.Bool: p.BoolParameter.Value = Get<bool>(a, target); break;
                case Kind.Int: p.IntParameter.Value = Get<int>(a, target); break;
                case Kind.Float: p.FloatParameter.Value = Get<float>(a, target); break;
                case Kind.String: p.StringParameter.Value = (string)Get<object>(a, target); break;
                case Kind.Vector2: p.Vector2Parameter.Value = Get<Vector2>(a, target); break;
                case Kind.Vector3: p.Vector3Parameter.Value = Get<Vector3>(a, target); break;
                case Kind.Rect: p.RectParamater.Value = Get<Rect>(a, target); break;
                case Kind.Quaternion: p.QuaternionParameter.Value = Get<Quaternion>(a, target); break;
                case Kind.GameObject: p.GameObjectParameter.Value = (GameObject)Get<object>(a, target); break;
                case Kind.Material: p.MaterialParameter.Value = (Material)Get<object>(a, target); break;
                case Kind.Texture: p.TextureParameter.Value = (Texture)Get<object>(a, target); break;
                case Kind.Color: p.ColorParameter.Value = Get<Color>(a, target); break;
                case Kind.UnityObject: p.ObjectParameter.Value = (UnityEngine.Object)Get<object>(a, target); break;
            }
        }

        /// <summary>FsmProperty.SetValue() for the supported kinds. Returns false if the original must run.</summary>
        static bool DoSet(FsmProperty p, Accessor a, object target)
        {
            if (a.Setter == null) return false; // reflection throws its own exception
            switch (a.Kind)
            {
                case Kind.Bool: { FsmBool v = p.BoolParameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.Int: { FsmInt v = p.IntParameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.Float: { FsmFloat v = p.FloatParameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.String: { FsmString v = p.StringParameter; if (!v.IsNone) Set<object>(a, target, v.Value); return true; }
                case Kind.Vector2: { FsmVector2 v = p.Vector2Parameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.Vector3: { FsmVector3 v = p.Vector3Parameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.Rect: { FsmRect v = p.RectParamater; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.Quaternion: { FsmQuaternion v = p.QuaternionParameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                // GameObject, Material and Texture are UnityEngine.Object subclasses: with their own parameter set to
                // None the original falls through to the "IsSubclassOf(UnityEngine.Object)" branch (ObjectParameter).
                case Kind.GameObject: { FsmGameObject v = p.GameObjectParameter; if (!v.IsNone) { Set<object>(a, target, v.Value); return true; } goto case Kind.UnityObject; }
                case Kind.Material: { FsmMaterial v = p.MaterialParameter; if (!v.IsNone) { Set<object>(a, target, v.Value); return true; } goto case Kind.UnityObject; }
                case Kind.Texture: { FsmTexture v = p.TextureParameter; if (!v.IsNone) { Set<object>(a, target, v.Value); return true; } goto case Kind.UnityObject; }
                case Kind.Color: { FsmColor v = p.ColorParameter; if (!v.IsNone) Set(a, target, v.Value); return true; }
                case Kind.UnityObject:
                    {
                        FsmObject v = p.ObjectParameter;
                        if (v.IsNone) return true;
                        // Original: ObjectParameter.Value == null (Unity null) ? null : ObjectParameter.Value.
                        UnityEngine.Object value = v.Value;
                        object arg = value == null ? null : value;
                        if (arg != null && !a.ValueType.IsInstanceOfType(arg)) return false; // reflection would throw
                        Set(a, target, arg);
                        return true;
                    }
            }
            return false;
        }

        // ----- Patches ------------------------------------------------------------------------------------

        static bool GetPrefix(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            GetCalls++;
            Accessor a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out bool nothing);
            if (nothing) return false;
            if (a == null) { if (FsmFastPath.DetailedStats) NoteSlow("get", __instance, ___memberInfo, ___initialized, ___targetObjectCached); return true; }
            GetFast++;
            DoGet(__instance, a, ___targetObjectCached);
            return false;
        }

        static bool SetPrefix(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            SetCalls++;
            Accessor a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out bool nothing);
            if (nothing) return false;
            if (a == null) { if (FsmFastPath.DetailedStats) NoteSlow("set", __instance, ___memberInfo, ___initialized, ___targetObjectCached); return true; }
            if (!DoSet(__instance, a, ___targetObjectCached)) { if (FsmFastPath.DetailedStats) NoteSlow("set", __instance, ___memberInfo, ___initialized, ___targetObjectCached); return true; }
            SetFast++;
            return false;
        }

        // ----- Diagnostics (detailed statistics only): why did a call take the original path? -------------

        public static readonly Dictionary<string, long> SlowReasons = new Dictionary<string, long>();

        static void NoteSlow(string op, FsmProperty p, MemberInfo[] infos, bool initialized, UnityEngine.Object cached)
        {
            string reason;
            try
            {
                FsmObject t = p.TargetObject;
                if (!initialized || t == null || cached != t.Value) reason = "reinit (target changed)";
                else if (t.UseVariable && !ReferenceEquals(p.TargetType, t.ObjectType)) reason = "reinit (variable type)";
                else if (infos == null || infos.Length != 1) reason = "member path length " + (infos == null ? 0 : infos.Length);
                else
                {
                    MemberInfo m = infos[0];
                    string type = p.PropertyType != null ? p.PropertyType.Name : "null";
                    reason = $"{m.DeclaringType?.Name}.{m.Name} ({type}, {m.MemberType})";
                }
            }
            catch (Exception ex) { reason = "error " + ex.GetType().Name; }
            string key = op + ": " + reason;
            SlowReasons.TryGetValue(key, out long n);
            if (n == 0 && SlowReasons.Count > 200) return;
            SlowReasons[key] = n + 1;
        }

        // Shadow: the original always runs. GetValue: the fast value is computed first and compared afterwards.
        static FsmProperty shadowProperty;
        static object shadowValue;

        static object ReadParameter(FsmProperty p, Accessor a)
        {
            switch (a.Kind)
            {
                case Kind.Bool: return p.BoolParameter.Value;
                case Kind.Int: return p.IntParameter.Value;
                case Kind.Float: return p.FloatParameter.Value;
                case Kind.String: return p.StringParameter.Value;
                case Kind.Vector2: return p.Vector2Parameter.Value;
                case Kind.Vector3: return p.Vector3Parameter.Value;
                case Kind.Rect: return p.RectParamater.Value;
                case Kind.Quaternion: return p.QuaternionParameter.Value;
                case Kind.GameObject: return p.GameObjectParameter.Value;
                case Kind.Material: return p.MaterialParameter.Value;
                case Kind.Texture: return p.TextureParameter.Value;
                case Kind.Color: return p.ColorParameter.Value;
                default: return p.ObjectParameter.Value;
            }
        }

        static object FastValue(Accessor a, object target)
        {
            switch (a.Kind)
            {
                case Kind.Bool: return Get<bool>(a, target);
                case Kind.Int: return Get<int>(a, target);
                case Kind.Float: return Get<float>(a, target);
                case Kind.Vector2: return Get<Vector2>(a, target);
                case Kind.Vector3: return Get<Vector3>(a, target);
                case Kind.Rect: return Get<Rect>(a, target);
                case Kind.Quaternion: return Get<Quaternion>(a, target);
                case Kind.Color: return Get<Color>(a, target);
                default: return Get<object>(a, target);
            }
        }

        static Accessor shadowAccessor;

        static bool GetPrefixShadow(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            GetCalls++;
            shadowProperty = null;
            Accessor a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out bool _);
            if (a == null) return true;
            GetFast++;
            try { shadowValue = FastValue(a, ___targetObjectCached); }
            catch { return true; }
            shadowProperty = __instance;
            shadowAccessor = a;
            return true;
        }

        static void GetPostfixShadow(FsmProperty __instance)
        {
            if (!ReferenceEquals(shadowProperty, __instance)) return;
            shadowProperty = null;
            ShadowChecked++;
            object actual = ReadParameter(__instance, shadowAccessor);
            if (Equals(actual, shadowValue)) return;
            ShadowMismatch++;
            if (MismatchLog.Count < 20)
                MismatchLog.Add($"{shadowAccessor.DeclaringType.Name}.{__instance.PropertyName}: fast {shadowValue} vs original {actual}");
        }

        static bool SetPrefixShadow(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            SetCalls++;
            Accessor a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out bool _);
            if (a != null && a.Setter != null) SetFast++;
            return true;
        }

        public static void AppendReport(StringBuilder sb)
        {
            double pct(long a, long b) => b > 0 ? 100.0 * a / b : 0.0;
            sb.AppendLine($"FsmProperty fast path: {Status}; get {GetCalls} calls ({pct(GetFast, GetCalls):0.0}% fast), set {SetCalls} calls ({pct(SetFast, SetCalls):0.0}% fast)" +
                          (Shadow ? $", shadow checked {ShadowChecked}, MISMATCHES {ShadowMismatch}" : ""));
            foreach (string s in MismatchLog) sb.AppendLine("  " + s);
            if (SlowReasons.Count > 0)
            {
                List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(SlowReasons);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                sb.AppendLine("  original path (detailed statistics):");
                for (int i = 0; i < list.Count && i < 15; i++) sb.AppendLine($"    {list[i].Value,8}  {list[i].Key}");
            }
        }
    }
}
