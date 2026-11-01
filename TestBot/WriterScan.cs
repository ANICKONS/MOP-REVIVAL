// Writer scan for the MOPR6 change ledger (task 20, design §3 "Внешние изменения"): which PlayMaker actions of the
// game write the properties the ledger records (active state, enabled, kinematic, layer, parent...), how often they
// occur and, for the reflection writers (SetProperty, CallMethod, InvokeMethod, SendMessage), which members they
// write or call. Also the count of every action type (input for the script sleep work, §7).
// Covers every FSM of the scene, active or not. Actions of FSMs that have not loaded them are loaded into a throwaway
// array (the state keeps its own), which needs the state's Fsm reference: for a dump-only vanilla run.
// Output: writers_<label>.txt.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class WriterScan
    {
        /// <summary>Action types that write a property kind of the ledger, with the kind.</summary>
        static readonly Dictionary<string, string> Writers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ActivateGameObject", "GoActive" }, { "Blink", "GoActive / RendererEnabled" }, { "Flicker", "GoActive / RendererEnabled" },
            { "EnableBehaviour", "BehaviourEnabled / ColliderEnabled / RendererEnabled" }, { "EnableFSM", "FsmEnabled" },
            { "SetVisibility", "RendererEnabled" }, { "EnableAnimation", "(AnimationState.enabled)" }, { "EnableGUI", "(PlayMakerGUI)" },
            { "CutToCamera", "BehaviourEnabled (Camera)" }, { "SetCharacterMotorProperties", "BehaviourEnabled" },
            { "SetLayer", "Layer" }, { "AnimationSettings", "Layer (animation layer, not GameObject)" },
            { "SetIsKinematic", "RbKinematic" }, { "UseGravity", "RbUseGravity" }, { "ControllerSettings", "RbDetectCollisions (CharacterController)" },
            { "Sleep", "RbSleep" }, { "WakeUp", "RbSleep" }, { "WakeAllRigidBodies", "RbSleep (all)" },
            { "SetParent", "Parent / LocalPose" }, { "SetPosition", "LocalPose" }, { "SetRotation", "LocalPose" },
            { "SetRandomRotation", "LocalPose" }, { "Translate", "LocalPose" }, { "Rotate", "LocalPose" }, { "LookAt", "LocalPose" },
            { "SmoothLookAt", "LocalPose" }, { "SmoothLookAtDirection", "LocalPose" }, { "SmoothFollowAction", "LocalPose" },
            { "MoveTowards", "LocalPose" }, { "SetHandsPosition", "LocalPose" },
            { "DestroyObject", "(target gone)" }, { "DestroySelf", "(target gone)" }, { "DestroyComponent", "(component gone)" },
            { "AddComponent", "(component added)" }, { "SetProperty", "reflection" }, { "CallMethod", "reflection" },
            { "InvokeMethod", "reflection" }, { "SendMessage", "reflection" }, { "CallStaticMethod", "reflection" }
        };

        public static string Run()
        {
            Dictionary<string, int> all = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> details = new Dictionary<string, int>(StringComparer.Ordinal);
            int fsms = 0, states = 0, loadedHere = 0, errors = 0;
            string firstError = null;

            foreach (PlayMakerFSM pm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (pm == null || pm.hideFlags != HideFlags.None || pm.gameObject.hideFlags != HideFlags.None) continue;
                Fsm fsm = pm.Fsm;
                if (fsm == null || fsm.States == null) continue;
                fsms++;
                foreach (FsmState s in fsm.States)
                {
                    states++;
                    FsmStateAction[] actions;
                    try
                    {
                        if (s.ActionsLoaded) actions = s.Actions;
                        else
                        {
                            if (s.Fsm == null) s.Fsm = fsm;
                            actions = s.ActionData.LoadActions(s);   // a copy: s.Actions stays unloaded
                            loadedHere++;
                        }
                    }
                    catch (Exception e)
                    {
                        errors++;
                        if (firstError == null) firstError = BotRunner.PathOf(pm.transform) + "#" + pm.FsmName + "/" + s.Name + ": " + e.GetType().Name + ": " + e.Message;
                        continue;
                    }
                    if (actions == null) continue;
                    foreach (FsmStateAction a in actions)
                    {
                        if (a == null) continue;
                        string type = a.GetType().Name;
                        Bump(all, type);
                        if (!Writers.ContainsKey(type)) continue;
                        string d = Detail(a);
                        if (d != null) Bump(details, type + ": " + d);
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"FSMs {fsms}, states {states}, states loaded for the scan {loadedHere}, errors {errors}" + (firstError != null ? " (first: " + firstError + ")" : ""));
            sb.AppendLine();
            sb.AppendLine("== Writers of ledger properties (action type: count -> kind)");
            foreach (KeyValuePair<string, string> w in Writers.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{(all.TryGetValue(w.Key, out int n) ? n : 0),7}  {w.Key} -> {w.Value}");
            sb.AppendLine();
            sb.AppendLine("== Details (reflection targets, behaviours, FSM names)");
            foreach (KeyValuePair<string, int> kv in details.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{kv.Value,7}  {kv.Key}");
            sb.AppendLine();
            sb.AppendLine("== Every action type");
            foreach (KeyValuePair<string, int> kv in all.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{kv.Value,7}  {kv.Key}");
            return sb.ToString();
        }

        static void Bump(Dictionary<string, int> d, string key) => d[key] = (d.TryGetValue(key, out int n) ? n : 0) + 1;

        // The actions live in the game's Assembly-CSharp, which the bot does not reference: fields by name.
        static object F(object o, string field) => o?.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public)?.GetValue(o);
        static bool B(object o, string field) => F(o, field) is bool b ? b : (F(o, field) is FsmBool fb && fb.Value);
        static string S(object o, string field) => F(o, field) is FsmString fs ? fs.Value : F(o, field) as string;

        static string Detail(FsmStateAction a)
        {
            switch (a.GetType().Name)
            {
                case "SetProperty":
                    {
                        if (!(F(a, "targetProperty") is FsmProperty p)) return "null";
                        string t = !string.IsNullOrEmpty(p.TargetTypeName) ? p.TargetTypeName : p.TargetType?.FullName ?? "?";
                        return Short(t) + "." + p.PropertyName + (B(a, "everyFrame") ? " (every frame)" : "");
                    }
                case "EnableBehaviour":
                    return F(a, "component") is Component c && c != null ? c.GetType().Name : S(a, "behaviour") ?? "?";
                case "CallMethod":
                    {
                        FsmObject fo = F(a, "behaviour") as FsmObject;
                        UnityEngine.Object o = fo?.Value;
                        string t = o != null ? o.GetType().Name : Short(fo?.ObjectType?.FullName ?? "?");
                        return t + "." + (S(a, "methodName") ?? "?") + (B(a, "everyFrame") ? " (every frame)" : "");
                    }
                case "InvokeMethod":
                    return (S(a, "behaviour") ?? "?") + "." + (S(a, "methodName") ?? "?");
                case "SendMessage":
                    return F(a, "functionCall") is FunctionCall fc ? fc.FunctionName : "?";
                case "EnableFSM":
                    return S(a, "fsmName") ?? "?";
                case "ActivateGameObject":
                    return (B(a, "recursive") ? "recursive" : "self") + (B(a, "everyFrame") ? ", every frame" : "") + (B(a, "resetOnExit") ? ", reset on exit" : "");
                case "Blink":
                case "Flicker":
                    return (B(a, "rendererOnly") ? "renderer" : "GameObject") + (B(a, "realTime") ? ", real time" : "");
                default:
                    return null;
            }
        }

        static string Short(string typeName)
        {
            if (typeName == null) return "?";
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }
    }
}
