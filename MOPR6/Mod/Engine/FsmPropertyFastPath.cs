// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// FsmProperty fast path (exact optimization, design §6; ported from MOPR 5.x Next/Engine/FsmPropertyFastPath.cs).
// PlayMaker's GetProperty / SetProperty actions (~130 calls per frame in MSC) go through System.Reflection every call
// (PropertyInfo.GetValue/SetValue, a boxed value and an argument array): the TestBot allocation profiler showed ~80%
// of all PlayMaker garbage (300-430 KB/s), which drives the ~110 ms GC freezes.
// The fast path calls the SAME getter / setter through a compiled delegate. It replicates FsmProperty 1.6 line by line
// (decompiled: _reference/decomp/PlayMaker/HutongGames.PlayMaker/FsmProperty.cs) for the common case only; everything
// else runs the original:
//   * CheckForReinitialize() would call Init()             -> original
//   * targetObjectCached == null || memberInfo == null       -> nothing (as the original)
//   * exactly one member in the path (a path "a.b" goes through ReflectionUtils with its own rules - and its
//     TargetException when "a" is null, task 24: the Satsuma's wheel FSMs read Wheel.physicMaterial.name);
//   * a public instance field / property whose type equals PropertyType and is one of bool, int, float, string,
//     Vector2, Vector3, Rect, Quaternion, Color, GameObject, Material, Texture or another UnityEngine.Object subclass
//     (for these exactly one branch of the original if/else chain matches);
//   * the target is an instance of the declaring type (else reflection throws its own exception);
//   * the FSM of the action is in the vanilla inventory (requirement 2.1; cached per FsmProperty).
// Property getter / setter exceptions are wrapped in TargetInvocationException, exactly like reflection does, and
// reach the game as they would. Verify: the original always runs; GetValue's fast value is computed first and
// compared with what the original stored (getters run twice in this mode only); SetValue is counted.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using MOPR6.Catalog;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Engine
{
    public sealed class FsmPropertyFastPath : Optimization
    {
        public static readonly FsmPropertyFastPath Instance = new FsmPropertyFastPath();
        public const string SystemId = "engine.property";
        const string HarmonyId = "mopr.engine.property";

        FsmPropertyFastPath() : base(SystemId) { }

        public override string Title => "FsmProperty fast path";
        public override Tier Tier => Tier.Exact;
        public override bool DefaultOn => true;

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

        sealed class Entry
        {
            public MemberInfo[] Infos;
            public Accessor Accessor;
            public Vanilla.Verdict Vanilla;
        }

        sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<FsmProperty, Entry> entries = new Dictionary<FsmProperty, Entry>(512, new RefComparer<FsmProperty>());
        static readonly Dictionary<MemberInfo, Accessor> accessors = new Dictionary<MemberInfo, Accessor>(new RefComparer<MemberInfo>());
        static readonly Accessor Unsupported = new Accessor();
        static readonly Vanilla.Verdict NotVanilla = new Vanilla.Verdict { Value = false };
        static readonly Dictionary<Type, Kind> kinds = new Dictionary<Type, Kind>
        {
            { typeof(bool), Kind.Bool }, { typeof(int), Kind.Int }, { typeof(float), Kind.Float }, { typeof(string), Kind.String },
            { typeof(Vector2), Kind.Vector2 }, { typeof(Vector3), Kind.Vector3 }, { typeof(Rect), Kind.Rect },
            { typeof(Quaternion), Kind.Quaternion }, { typeof(Color), Kind.Color }, { typeof(GameObject), Kind.GameObject },
            { typeof(Material), Kind.Material }, { typeof(Texture), Kind.Texture }
        };

        static HarmonyInstance harmony;
        static bool verify;
        public static long GetCalls, GetFast, SetCalls, SetFast, Checked, Mismatched;
        static readonly List<string> mismatchLog = new List<string>();

        protected internal override string OnInstall(SystemMode mode)
        {
            if (!Vanilla.Ready) return "no vanilla inventory (" + Vanilla.Error + ")";
            MethodInfo get = typeof(FsmProperty).GetMethod("GetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            MethodInfo set = typeof(FsmProperty).GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
            if (get == null || set == null ||
                typeof(FsmProperty).GetField("initialized", I)?.FieldType != typeof(bool) ||
                typeof(FsmProperty).GetField("targetObjectCached", I)?.FieldType != typeof(UnityEngine.Object) ||
                typeof(FsmProperty).GetField("memberInfo", I)?.FieldType != typeof(MemberInfo[]))
                return "FsmProperty layout changed";

            verify = mode == SystemMode.Verify;
            GetCalls = GetFast = SetCalls = SetFast = Checked = Mismatched = 0;
            mismatchLog.Clear();
            entries.Clear();
            shadowProperty = null;
            harmony = HarmonyInstance.Create(HarmonyId);
            if (verify)
            {
                harmony.Patch(get, Hook(nameof(GetPrefixVerify)), Hook(nameof(GetPostfixVerify)), null);
                harmony.Patch(set, Hook(nameof(SetPrefixVerify)), null, null);
            }
            else
            {
                harmony.Patch(get, Hook(nameof(GetPrefix)), null, null);
                harmony.Patch(set, Hook(nameof(SetPrefix)), null, null);
            }
            return null;
        }

        protected internal override void OnRemove()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            entries.Clear();
            shadowProperty = null;
        }

        /// <summary>FsmProperty objects of the old scene: gone (the accessors per member stay, they hold no objects).</summary>
        protected internal override void OnScene()
        {
            entries.Clear();
            shadowProperty = null;
        }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(FsmPropertyFastPath).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(FsmPropertyFastPath), name);
            return new HarmonyMethod(m);
        }

        // ----- Accessors --------------------------------------------------------------------------------------

        static Entry Resolve(FsmProperty property, MemberInfo[] infos)
        {
            Entry e;
            if (entries.TryGetValue(property, out e) && ReferenceEquals(e.Infos, infos)) return e;

            Accessor acc = Unsupported;
            if (infos.Length == 1 && infos[0] != null)
            {
                MemberInfo m = infos[0];
                if (!accessors.TryGetValue(m, out acc))
                {
                    // One odd member (a generic declaring type, a member Mono cannot emit for) stays with the original.
                    try { acc = Build(m) ?? Unsupported; }
                    catch (Exception ex)
                    {
                        Diagnostics.ModLog.Report(SystemId + "/Build", ex, m.DeclaringType + "." + m.Name);
                        acc = Unsupported;
                    }
                    accessors[m] = acc;
                }
            }
            // The FSM of the action that calls: an FsmProperty belongs to one action of one FSM.
            Vanilla.Verdict v = e != null ? e.Vanilla : null;
            if (v == null)
            {
                Fsm fsm = FsmExecutionStack.ExecutingFsm;
                PlayMakerFSM owner = fsm != null ? fsm.Owner as PlayMakerFSM : null;
                v = owner != null ? Vanilla.For(owner) : NotVanilla;
            }
            if (entries.Count > 20000) entries.Clear();
            e = new Entry { Infos = infos, Accessor = acc, Vanilla = v };
            entries[property] = e;
            return e;
        }

        /// <summary>A compiled getter / setter for one member, or null when the fast path does not apply to it.</summary>
        static Accessor Build(MemberInfo m)
        {
            Type declaring = m.DeclaringType;
            if (declaring == null || declaring.IsValueType || declaring.IsGenericTypeDefinition) return null;

            Type valueType;
            MethodInfo getter = null, setter = null;
            FieldInfo field = null;
            PropertyInfo p = m as PropertyInfo;
            FieldInfo f = m as FieldInfo;
            if (p != null)
            {
                if (p.GetIndexParameters().Length != 0) return null;
                getter = p.GetGetMethod(false);
                setter = p.GetSetMethod(false);
                if (getter == null || getter.IsStatic) return null;
                if (setter != null && setter.IsStatic) setter = null;
                valueType = p.PropertyType;
            }
            else if (f != null)
            {
                if (f.IsStatic || !f.IsPublic) return null;
                field = f;
                valueType = f.FieldType;
            }
            else return null;

            Kind kind;
            if (!kinds.TryGetValue(valueType, out kind))
            {
                // Original: the "PropertyType.IsSubclassOf(typeof(UnityEngine.Object))" branch (GameObject/Material/Texture come first).
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

        // ----- Shared checks ----------------------------------------------------------------------------------

        /// <summary>
        /// The accessor if the fast path applies, else null (run the original). <paramref name="nothing"/>: the original
        /// would do nothing at all (target or member missing).
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

            Entry e = Resolve(property, infos);
            Accessor acc = e.Accessor;
            if (acc == Unsupported || !e.Vanilla.Value || !ReferenceEquals(property.PropertyType, acc.ValueType)) return null;

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

        /// <summary>FsmProperty.GetValue() for the supported kinds (the same branch, parameter and setter).</summary>
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

        /// <summary>FsmProperty.SetValue() for the supported kinds. False: the original must run.</summary>
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
                // GameObject, Material and Texture are UnityEngine.Object subclasses: with their own parameter None the
                // original falls through to the "IsSubclassOf(UnityEngine.Object)" branch (ObjectParameter).
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

        static string Where(FsmProperty p)
        {
            Fsm fsm = FsmExecutionStack.ExecutingFsm;
            return (fsm != null && fsm.GameObject != null ? fsm.GameObject.name + "#" + fsm.Name : "?") + " " + p.TargetTypeName + "." + p.PropertyName;
        }

        // ----- Patches: On --------------------------------------------------------------------------------------
        // Our own checks are guarded (Fail); the game's getter / setter run outside the guard, so their exceptions
        // reach the game exactly as from the original.

        static bool GetPrefix(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            if (!Instance.Live) return true;
            Accessor a;
            bool nothing;
            try
            {
                GetCalls++;
                a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out nothing);
            }
            catch (Exception ex)
            {
                Instance.Fail("Get", ex, Where(__instance));
                return true;
            }
            if (nothing) return false;
            if (a == null) return true;
            GetFast++;
            DoGet(__instance, a, ___targetObjectCached);
            return false;
        }

        static bool SetPrefix(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            if (!Instance.Live) return true;
            Accessor a;
            bool nothing;
            try
            {
                SetCalls++;
                a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out nothing);
            }
            catch (Exception ex)
            {
                Instance.Fail("Set", ex, Where(__instance));
                return true;
            }
            if (nothing) return false;
            if (a == null || !DoSet(__instance, a, ___targetObjectCached)) return true;
            SetFast++;
            return false;
        }

        // ----- Patches: Verify ----------------------------------------------------------------------------------

        static FsmProperty shadowProperty;
        static Accessor shadowAccessor;
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

        static bool GetPrefixVerify(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            shadowProperty = null;
            if (!Instance.Live) return true;
            Accessor a;
            bool nothing;
            try
            {
                GetCalls++;
                a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out nothing);
            }
            catch (Exception ex)
            {
                Instance.Fail("Get/verify", ex, Where(__instance));
                return true;
            }
            if (a == null) return true;
            GetFast++;
            try { shadowValue = FastValue(a, ___targetObjectCached); }
            catch (TargetInvocationException) { return true; } // catch-ok: the game's getter threw; the original runs and throws the same
            shadowProperty = __instance;
            shadowAccessor = a;
            return true;
        }

        static void GetPostfixVerify(FsmProperty __instance)
        {
            if (!ReferenceEquals(shadowProperty, __instance)) return;
            shadowProperty = null;
            try
            {
                Checked++;
                object actual = ReadParameter(__instance, shadowAccessor);
                if (Equals(actual, shadowValue)) return;
                Mismatched++;
                if (mismatchLog.Count < 20) mismatchLog.Add(Where(__instance) + ": fast " + shadowValue + " vs original " + actual);
            }
            catch (Exception ex) { Instance.Fail("Get/check", ex, Where(__instance)); }
        }

        static bool SetPrefixVerify(FsmProperty __instance, UnityEngine.Object ___targetObjectCached, MemberInfo[] ___memberInfo, bool ___initialized)
        {
            if (!Instance.Live) return true;
            try
            {
                SetCalls++;
                bool nothing;
                Accessor a = Check(__instance, ___targetObjectCached, ___memberInfo, ___initialized, out nothing);
                if (a != null && a.Setter != null) SetFast++;
            }
            catch (Exception ex) { Instance.Fail("Set/verify", ex, Where(__instance)); }
            return true;
        }

        // ----- Report -------------------------------------------------------------------------------------------

        public override long Mismatches => Mismatched;

        public override void Report(StringBuilder sb)
        {
            double pg = GetCalls > 0 ? 100.0 * GetFast / GetCalls : 0, ps = SetCalls > 0 ? 100.0 * SetFast / SetCalls : 0;
            sb.Append("  get ").Append(GetCalls).Append(" (").Append(pg.ToString("0.0")).Append("% fast), set ").Append(SetCalls)
              .Append(" (").Append(ps.ToString("0.0")).Append("% fast)");
            if (verify) sb.Append(", checked ").Append(Checked).Append(", mismatches ").Append(Mismatched);
            sb.Append('\n');
            foreach (string s in mismatchLog) sb.Append("  MISMATCH ").Append(s).Append('\n');
        }
    }
}
