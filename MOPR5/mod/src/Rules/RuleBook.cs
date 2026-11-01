// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Rules (5.1): every active rule set compiled into lookups. One dictionary lookup per question, no allocation
// (MOP 3.10 built a new list of all rules and scanned it for every object it set up).
//
// When two sets say different things about the same object, the rule that lets MOP do less wins:
// exclude > keepActive. (MOP 3.10 took whichever file came first in the folder.)
//
// Pure C#: no Unity, no MSCLoader.

using System;
using System.Collections.Generic;
using System.Text;

namespace MOP.Rules
{
    enum ObjectRule { None = 0, KeepActive = 1, Exclude = 2 }

    class RuleBook
    {
        sealed class ObjectEntry
        {
            public ObjectRule Rule;
            public readonly List<string> Sources = new List<string>();
            public int Hits;
        }

        static readonly string[] NoNames = new string[0];

        readonly Dictionary<string, ObjectEntry> objects = new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, string[]> keepActiveIn = new Dictionary<string, string[]>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> keepActiveInSources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> noLod = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public readonly List<ManageRule> Manage = new List<ManageRule>();
        public readonly List<ReparentRule> Reparent = new List<ReparentRule>();
        public readonly List<SectorRule> Sectors = new List<SectorRule>();

        public bool SatsumaExclude { get; private set; }
        /// <summary>MOP never hides the Satsuma's parts (also true when the car is excluded).</summary>
        public bool SatsumaKeepRenderers { get; private set; }
        public bool KeepEmptyBottles { get; private set; }
        public bool SkipFuryColliderFix { get; private set; }
        public bool AllVehiclesPhysicsOnly { get; private set; }
        public bool NoLods { get; private set; }

        public readonly List<RuleSet> Active = new List<RuleSet>();
        public readonly List<RuleSet> Inactive = new List<RuleSet>();

        public static readonly RuleBook Empty = new RuleBook();

        public static RuleBook Compile(IEnumerable<RuleSet> active, IEnumerable<RuleSet> inactive)
        {
            RuleBook book = new RuleBook();
            if (inactive != null) book.Inactive.AddRange(inactive);
            if (active == null) return book;

            foreach (RuleSet set in active)
            {
                book.Active.Add(set);
                string source = set.Name;

                foreach (string name in set.KeepActive) book.AddObject(name, ObjectRule.KeepActive, source);
                foreach (string name in set.Exclude) book.AddObject(name, ObjectRule.Exclude, source);

                foreach (ChildRule c in set.KeepActiveIn)
                {
                    book.keepActiveIn.TryGetValue(c.Parent, out string[] children);
                    if (children == null || Array.IndexOf(children, c.Name) < 0)
                    {
                        string[] grown = new string[(children?.Length ?? 0) + 1];
                        if (children != null) children.CopyTo(grown, 0);
                        grown[grown.Length - 1] = c.Name;
                        book.keepActiveIn[c.Parent] = grown;
                    }
                    AddSource(book.keepActiveInSources, c.Parent, source);
                }

                foreach (string name in set.NoLod) AddSource(book.noLod, name, source);

                foreach (ManageRule m in set.Manage)
                {
                    bool known = false;
                    for (int i = 0; i < book.Manage.Count && !known; i++)
                        known = book.Manage[i].Name == m.Name && book.Manage[i].Kind == m.Kind;
                    if (!known) book.Manage.Add(m);
                }

                book.Reparent.AddRange(set.Reparent);
                book.Sectors.AddRange(set.Sectors);

                if (set.Satsuma == SatsumaRule.Exclude) book.SatsumaExclude = true;
                if (set.Satsuma != SatsumaRule.None) book.SatsumaKeepRenderers = true;
                book.KeepEmptyBottles |= set.KeepEmptyBottles;
                book.SkipFuryColliderFix |= set.SkipFuryColliderFix;
                book.AllVehiclesPhysicsOnly |= set.AllVehiclesPhysicsOnly;
                book.NoLods |= set.NoLods;
            }
            return book;
        }

        void AddObject(string name, ObjectRule rule, string source)
        {
            if (!objects.TryGetValue(name, out ObjectEntry e))
            {
                e = new ObjectEntry();
                objects[name] = e;
            }
            if (rule > e.Rule) e.Rule = rule;
            if (!e.Sources.Contains(source)) e.Sources.Add(source);
        }

        static void AddSource(Dictionary<string, List<string>> map, string key, string source)
        {
            if (!map.TryGetValue(key, out List<string> list))
            {
                list = new List<string>();
                map[key] = list;
            }
            if (!list.Contains(source)) list.Add(source);
        }

        // ===== Questions (called while MOP sets up objects) ==============================================

        public bool HasObjectRules => objects.Count > 0;

        /// <summary>What the rules say about an object, by its exact name.</summary>
        public ObjectRule Object(string name)
        {
            if (name == null || objects.Count == 0) return ObjectRule.None;
            if (!objects.TryGetValue(name, out ObjectEntry e)) return ObjectRule.None;
            e.Hits++;
            return e.Rule;
        }

        /// <summary>Any rule (exclude or keepActive) for this object.</summary>
        public bool HasObjectRule(string name) => Object(name) != ObjectRule.None;

        /// <summary>Children of a place or vehicle that stay on while MOP switches it off.</summary>
        public string[] KeepActiveChildren(string parent)
        {
            if (parent == null || keepActiveIn.Count == 0) return NoNames;
            return keepActiveIn.TryGetValue(parent, out string[] children) ? children : NoNames;
        }

        public bool IsNoLod(string name) => name != null && noLod.Count > 0 && noLod.ContainsKey(name);

        public int RuleCount
        {
            get
            {
                int n = 0;
                foreach (RuleSet s in Active) n += s.RuleCount;
                return n;
            }
        }

        // ===== Diagnostics ===============================================================================

        /// <summary>Object rules that matched nothing since the book was compiled (a typo, or the object is not in the game).</summary>
        public List<string> Unmatched()
        {
            List<string> list = new List<string>();
            foreach (KeyValuePair<string, ObjectEntry> kv in objects)
                if (kv.Value.Hits == 0) list.Add(kv.Key + " (" + string.Join(", ", kv.Value.Sources.ToArray()) + ")");
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>Every rule that names this object, one per line ("" if none).</summary>
        public string Why(string name)
        {
            StringBuilder sb = new StringBuilder();
            if (objects.TryGetValue(name, out ObjectEntry e))
                sb.Append(e.Rule == ObjectRule.Exclude ? "exclude" : "keepActive").Append(" (").Append(string.Join(", ", e.Sources.ToArray()))
                  .Append(e.Hits > 0 ? $"), matched {e.Hits}x\n" : "), not matched yet\n");
            if (keepActiveIn.TryGetValue(name, out string[] children))
                sb.Append("keepActiveIn: ").Append(string.Join(", ", children)).Append(" (")
                  .Append(string.Join(", ", keepActiveInSources[name].ToArray())).Append(")\n");
            foreach (KeyValuePair<string, string[]> kv in keepActiveIn)
                if (Array.IndexOf(kv.Value, name) >= 0)
                    sb.Append("stays on inside ").Append(kv.Key).Append(" (").Append(string.Join(", ", keepActiveInSources[kv.Key].ToArray())).Append(")\n");
            foreach (ManageRule m in Manage)
                if (m.Name == name) sb.Append("managed as ").Append(m.Kind).Append('\n');
            foreach (ReparentRule r in Reparent)
                if (r.Name == name) sb.Append("moved under ").Append(r.Parent ?? "the scene root").Append(" at load\n");
            if (noLod.TryGetValue(name, out List<string> lodSources))
                sb.Append("noLod (").Append(string.Join(", ", lodSources.ToArray())).Append(")\n");
            foreach (SectorRule s in Sectors)
                if (s.KeepVisible.Contains(name)) sb.Append("stays visible in a rule sector\n");
            return sb.ToString().TrimEnd('\n');
        }
    }
}
