// Trace of exceptions thrown by PlayMaker's FsmProperty (GetProperty / SetProperty actions): which FSM, state,
// action and property path threw, and which member of the path returned null. For the MOPR6 investigation of the
// TargetException seen in 5.x runs (task 24): the exception comes out of PlayMakerFSM.Update without any FSM name.
//
// How: a prefix on FsmProperty.GetValue/SetValue remembers the property and the executing FSM / state / action, the
// postfix forgets it. Harmony runs no postfix when the original throws, so when Unity logs an exception whose stack
// goes through FsmProperty, the remembered property is the one that threw. The path is then walked again member by
// member (only on that error path) to find the first null.
// Costs two static writes per call while on; the patches stay installed for the process, On switches them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class PropTrace
    {
        const string HarmonyId = "mopr.testbot.proptrace";

        static HarmonyInstance harmony;
        static FieldInfo memberInfoField, cachedField;
        public static bool On;

        static FsmProperty current;
        static bool currentSet;
        static Fsm currentFsm;
        static FsmState currentState;
        static FsmStateAction currentAction;

        sealed class Hit { public int Count; public int FirstFrame; public string Message; }
        static readonly Dictionary<string, Hit> hits = new Dictionary<string, Hit>();
        public static int Exceptions, Unattributed;

        public static string Install()
        {
            if (harmony != null) return "already installed";
            const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
            memberInfoField = typeof(FsmProperty).GetField("memberInfo", I);
            cachedField = typeof(FsmProperty).GetField("targetObjectCached", I);
            MethodInfo get = typeof(FsmProperty).GetMethod("GetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            MethodInfo set = typeof(FsmProperty).GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (get == null || set == null || memberInfoField == null || cachedField == null) return "FsmProperty layout changed";

            harmony = HarmonyInstance.Create(HarmonyId);
            HarmonyMethod post = new HarmonyMethod(typeof(PropTrace).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(get, new HarmonyMethod(typeof(PropTrace).GetMethod(nameof(GetPrefix), BindingFlags.Static | BindingFlags.NonPublic)), post, null);
            harmony.Patch(set, new HarmonyMethod(typeof(PropTrace).GetMethod(nameof(SetPrefix), BindingFlags.Static | BindingFlags.NonPublic)), post, null);
            Application.logMessageReceived += OnLog;
            return "patched FsmProperty.GetValue/SetValue";
        }

        static void GetPrefix(FsmProperty __instance) { if (On) Remember(__instance, false); }
        static void SetPrefix(FsmProperty __instance) { if (On) Remember(__instance, true); }

        static void Remember(FsmProperty p, bool set)
        {
            current = p;
            currentSet = set;
            currentFsm = FsmExecutionStack.ExecutingFsm;
            currentState = FsmExecutionStack.ExecutingState;
            currentAction = FsmExecutionStack.ExecutingAction;
        }

        static void Postfix() { current = null; }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || stackTrace == null || stackTrace.IndexOf("FsmProperty.", StringComparison.Ordinal) < 0) return;
            Exceptions++;
            FsmProperty p = current;
            current = null;
            if (p == null) { Unattributed++; return; }
            string key;
            try { key = Describe(p); }
            catch (Exception ex) { key = "describe failed: " + ex.GetType().Name; }
            if (!hits.TryGetValue(key, out Hit h)) hits[key] = h = new Hit { FirstFrame = BotRunner.SyncedFrame, Message = condition };
            h.Count++;
        }

        static string Describe(FsmProperty p)
        {
            StringBuilder sb = new StringBuilder();
            GameObject go = currentFsm?.GameObject;
            sb.Append(go != null ? BotRunner.PathOf(go.transform) : "?").Append(" | ").Append(currentFsm?.Name ?? "?")
              .Append(" | ").Append(currentState?.Name ?? "?").Append(" | ").Append(currentAction?.GetType().Name ?? "?")
              .Append(currentSet ? " | set " : " | get ").Append(p.TargetTypeName).Append('.').Append(p.PropertyName);

            UnityEngine.Object target = cachedField.GetValue(p) as UnityEngine.Object;
            sb.Append(" | target ").Append(target == null ? "null" : target.GetType().Name + " '" + target.name + "'");
            if (p.TargetObject != null && p.TargetObject.UseVariable) sb.Append(" (variable '").Append(p.TargetObject.Name).Append("')");

            // The first member of the path that returned null (the next one then has no target).
            MemberInfo[] infos = memberInfoField.GetValue(p) as MemberInfo[];
            if (infos != null && target != null)
            {
                object o = target;
                for (int i = 0; i < infos.Length - 1; i++)
                {
                    try { o = ReflectionUtils.GetMemberValue(infos[i], o); }
                    catch (Exception ex) { sb.Append(" | ").Append(infos[i].Name).Append(" threw ").Append(ex.GetType().Name); break; }
                    bool isNull = o == null || (o is UnityEngine.Object u && u == null);
                    if (isNull) { sb.Append(" | ").Append(infos[i].Name).Append(" is null"); break; }
                }
            }
            return sb.ToString();
        }

        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"FsmProperty exceptions: {Exceptions}, attributed {Exceptions - Unattributed}, unattributed {Unattributed}");
            foreach (KeyValuePair<string, Hit> kv in hits.OrderByDescending(k => k.Value.Count))
                sb.AppendLine($"{kv.Value.Count,6}  f={kv.Value.FirstFrame,-7} {kv.Key}  [{kv.Value.Message}]");
            return sb.ToString();
        }
    }
}
