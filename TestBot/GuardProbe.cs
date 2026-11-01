// Restart-reset probe ("guardmark" / "guardcheck"). For every running FSM under the given roots whose restart path
// (start state + states reachable within 3 transitions) has an Easy Save 2 Load action, the variable that action
// loads into gets a value the save does not have (float +0.123, int +7, bool flipped, string + "~", vector3 x +0.123).
// After MOP switched the objects off and on again, "guardcheck" reports each marked variable as kept or RESET:
// a reset means the FSM restarted and read the save again.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class GuardProbe
    {
        class Mark
        {
            public string Where;      // path#fsm/state/action
            public NamedVariable Var;
            public string Marked;     // value after marking
            public string Saved;      // value before (= what the save has)
        }

        static readonly List<Mark> marks = new List<Mark>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int MaxDepth = 3;

        static string Value(NamedVariable v)
        {
            switch (v)
            {
                case FsmFloat f: return f.Value.ToString("0.####", Inv);
                case FsmInt i: return i.Value.ToString(Inv);
                case FsmBool b: return b.Value ? "true" : "false";
                case FsmString s: return s.Value ?? "";
                case FsmVector3 v3: return v3.Value.x.ToString("0.####", Inv) + "," + v3.Value.y.ToString("0.####", Inv) + "," + v3.Value.z.ToString("0.####", Inv);
            }
            return null;
        }

        static bool Apply(NamedVariable v)
        {
            switch (v)
            {
                case FsmFloat f: f.Value += 0.123f; return true;
                case FsmInt i: i.Value += 7; return true;
                case FsmBool b: b.Value = !b.Value; return true;
                case FsmString s: s.Value = (s.Value ?? "") + "~"; return true;
                case FsmVector3 v3: v3.Value = v3.Value + new Vector3(0.123f, 0f, 0f); return true;
            }
            return false;
        }

        /// <summary>Marks under the roots named in <paramref name="roots"/> (";" separated - names contain commas; empty = every root).</summary>
        public static string MarkAll(string roots, out string summary)
        {
            marks.Clear();
            HashSet<string> rootNames = string.IsNullOrEmpty(roots) ? null
                : new HashSet<string>(roots.Split(';').Select(r => r.Trim()).Where(r => r.Length > 0), StringComparer.Ordinal);
            HashSet<NamedVariable> seen = new HashSet<NamedVariable>();
            int fsms = 0;
            StringBuilder sb = new StringBuilder();

            foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || fsm.hideFlags != HideFlags.None || !fsm.enabled || !fsm.gameObject.activeInHierarchy) continue;
                if (rootNames != null && !rootNames.Contains(fsm.transform.root.name)) continue;
                Fsm f = fsm.Fsm;
                if (f == null || !f.RestartOnEnable) continue; // MOP's fixes or the game already keep it from restarting

                bool any = false;
                foreach (KeyValuePair<FsmState, FsmStateAction> load in LoadActions(fsm, f))
                {
                    FieldInfo field = load.Value.GetType().GetField("loadValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    NamedVariable v = field?.GetValue(load.Value) as NamedVariable;
                    if (v == null || v.IsNone || !seen.Add(v)) continue;
                    string before = Value(v);
                    if (before == null || !Apply(v)) continue;
                    Mark m = new Mark
                    {
                        Where = BotRunner.PathOf(fsm.transform) + "#" + fsm.FsmName + " " + load.Key.Name + "/" + load.Value.GetType().Name + " " + v.Name,
                        Var = v, Saved = before, Marked = Value(v)
                    };
                    marks.Add(m);
                    sb.Append(m.Where).Append('\t').Append(m.Saved).Append(" -> ").Append(m.Marked).Append('\n');
                    any = true;
                }
                if (any) fsms++;
            }
            summary = $"{marks.Count} variables marked in {fsms} FSMs";
            return summary + "\n" + sb;
        }

        public static string Check(out string summary)
        {
            int kept = 0, reset = 0, other = 0;
            StringBuilder sb = new StringBuilder();
            foreach (Mark m in marks)
            {
                string now = Value(m.Var);
                string verdict = now == m.Marked ? "kept" : now == m.Saved ? "RESET" : "changed";
                if (verdict == "kept") kept++; else if (verdict == "RESET") reset++; else other++;
                sb.Append(verdict.PadRight(8)).Append(m.Where).Append('\t').Append(m.Marked).Append(" -> ").Append(now).Append('\n');
            }
            summary = $"{marks.Count} marked: {kept} kept, {reset} RESET to the save, {other} changed otherwise";
            return summary + "\n" + sb;
        }

        static IEnumerable<KeyValuePair<FsmState, FsmStateAction>> LoadActions(PlayMakerFSM fsm, Fsm f)
        {
            FsmState[] states = fsm.FsmStates;
            if (states == null) yield break;
            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            HashSet<string> visited = new HashSet<string>();
            queue.Enqueue(new KeyValuePair<string, int>(f.StartState, 0));
            while (queue.Count > 0)
            {
                KeyValuePair<string, int> next = queue.Dequeue();
                if (string.IsNullOrEmpty(next.Key) || !visited.Add(next.Key)) continue;
                FsmState state = states.FirstOrDefault(s => s.Name == next.Key);
                if (state == null || state.Fsm == null) continue;
                FsmStateAction[] actions = state.Actions;
                if (actions != null)
                    foreach (FsmStateAction a in actions)
                    {
                        if (a == null) continue;
                        string n = a.GetType().Name;
                        if (n.StartsWith("Load") && n != "LoadLevel" && n != "LoadTransform") yield return new KeyValuePair<FsmState, FsmStateAction>(state, a);
                    }
                if (next.Value >= MaxDepth || state.Transitions == null) continue;
                foreach (FsmTransition t in state.Transitions)
                    if (t != null && !string.IsNullOrEmpty(t.ToState)) queue.Enqueue(new KeyValuePair<string, int>(t.ToState, next.Value + 1));
            }
        }
    }
}
