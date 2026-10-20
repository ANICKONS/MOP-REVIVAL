// Measurement only: is SimpleIKSolver.LateUpdate a no-op once its inputs stop changing?
// Prefix/postfix on the game's SimpleIKSolver.LateUpdate record the local pose (position, rotation, scale) of
// every joint before and after the solve and count, per solver, solves that changed nothing (bitwise) and
// solves that changed something. A solve that changes nothing on unchanged inputs can be skipped exactly.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Harmony;
using UnityEngine;

namespace MOPR.TestBot
{
    static class IkProbe
    {
        const string HarmonyId = "com.anickon.mopr.testbot.ikprobe";
        static HarmonyInstance harmony;
        static FieldInfo fJoints, fJoint;

        sealed class Stat { public long Solves, NoOps, Changed; public string LastChange; }
        static readonly Dictionary<MonoBehaviour, Stat> stats = new Dictionary<MonoBehaviour, Stat>();
        static readonly List<Vector3> before = new List<Vector3>();
        static readonly List<Quaternion> beforeRot = new List<Quaternion>();

        public static string Install()
        {
            Uninstall();
            Type t = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("SimpleIKSolver", false); } catch { return null; } })
                .FirstOrDefault(x => x != null);
            if (t == null) return "SimpleIKSolver not found";
            MethodInfo late = t.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            fJoints = t.GetField("JointEntities");
            Type je = t.GetNestedType("JointEntity");
            fJoint = je != null ? je.GetField("Joint") : null;
            if (late == null || fJoints == null || fJoint == null) return "SimpleIKSolver members not found";
            stats.Clear();
            harmony = HarmonyInstance.Create(HarmonyId);
            harmony.Patch(late, new HarmonyMethod(typeof(IkProbe).GetMethod("Pre", BindingFlags.Static | BindingFlags.NonPublic)),
                new HarmonyMethod(typeof(IkProbe).GetMethod("Post", BindingFlags.Static | BindingFlags.NonPublic)), null);
            return null;
        }

        public static void Uninstall()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
        }

        static void Read(MonoBehaviour solver, List<Vector3> pos, List<Quaternion> rot)
        {
            pos.Clear();
            rot.Clear();
            Array joints = fJoints.GetValue(solver) as Array;
            if (joints == null) return;
            for (int i = 0; i < joints.Length; i++)
            {
                Transform j = fJoint.GetValue(joints.GetValue(i)) as Transform;
                if (j == null) continue;
                pos.Add(j.localPosition);
                pos.Add(j.localScale);
                rot.Add(j.localRotation);
            }
        }

        static void Pre(MonoBehaviour __instance) => Read(__instance, before, beforeRot);

        static readonly List<Vector3> after = new List<Vector3>();
        static readonly List<Quaternion> afterRot = new List<Quaternion>();

        static void Post(MonoBehaviour __instance)
        {
            Read(__instance, after, afterRot);
            if (!stats.TryGetValue(__instance, out Stat s)) stats[__instance] = s = new Stat();
            s.Solves++;
            bool same = after.Count == before.Count && afterRot.Count == beforeRot.Count;
            for (int i = 0; same && i < after.Count; i++)
                same = after[i].x.Equals(before[i].x) && after[i].y.Equals(before[i].y) && after[i].z.Equals(before[i].z);
            string change = null;
            for (int i = 0; same && i < afterRot.Count; i++)
            {
                Quaternion a = afterRot[i], b = beforeRot[i];
                same = a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z) && a.w.Equals(b.w);
                if (!same) change = $"joint {i}: {b.x:R},{b.y:R},{b.z:R},{b.w:R} -> {a.x:R},{a.y:R},{a.z:R},{a.w:R}";
            }
            if (same) s.NoOps++;
            else { s.Changed++; if (change != null) s.LastChange = change; }
        }

        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            long solves = 0, noops = 0;
            foreach (var kv in stats) { solves += kv.Value.Solves; noops += kv.Value.NoOps; }
            sb.Append($"{stats.Count} solvers, {solves} solves, {noops} changed nothing ({(solves > 0 ? 100.0 * noops / solves : 0):0.0}%)");
            foreach (var kv in stats.OrderBy(x => x.Key != null ? BotRunner.PathOf(x.Key.transform) : ""))
            {
                string path = kv.Key != null ? BotRunner.PathOf(kv.Key.transform) : "(destroyed)";
                sb.Append($"\n  {path}: {kv.Value.Solves} solves, no-op {kv.Value.NoOps}, changed {kv.Value.Changed}" +
                          (kv.Value.LastChange != null ? ", last change " + kv.Value.LastChange : ""));
            }
            return sb.ToString();
        }
    }
}
