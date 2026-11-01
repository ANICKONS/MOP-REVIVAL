// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Checks of the catalogue that need no game (design §5 "Проверка", requirement 11): the unit tests run them over the
// embedded files, so a rule with a typo never reaches a player.
//   * every rule has an id (unique), exactly one of match.path / match.class, a source and a test;
//   * the conditions and special cases it names exist (the code's registries);
//   * the test scenario file exists;
//   * the objects it matches exist in the vanilla inventory, as many as expect.count, with the expected components
//     and FSMs.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using MOPR6.Core.Snapshot;

namespace MOPR6.Core.Catalog
{
    public sealed class CatalogValidator
    {
        public readonly HashSet<string> KnownConditions = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> KnownSpecials = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Does a scenario file exist (relative path from the rule's "test")? Null = not checked.</summary>
        public Func<string, bool> TestExists;

        public List<string> Validate(IEnumerable<CatalogRule> rules, Inventory inventory)
        {
            List<string> problems = new List<string>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CatalogRule r in rules)
            {
                string id = string.IsNullOrEmpty(r.Id) ? "<no id>" : r.Id;
                if (string.IsNullOrEmpty(r.Id)) problems.Add("a rule without an id");
                else if (!ids.Add(r.Id)) problems.Add(id + ": duplicate id");
                if (string.IsNullOrEmpty(r.Source)) problems.Add(id + ": no source (which MOP 3.10 / 5.x fix or bug)");
                if (string.IsNullOrEmpty(r.Test)) problems.Add(id + ": no test scenario");
                else if (TestExists != null && !TestExists(r.Test)) problems.Add(id + ": test scenario not found: " + r.Test);
                if (r.Allow == null) problems.Add(id + ": no allow block");
                foreach (string w in r.When)
                    if (!KnownConditions.Contains(w)) problems.Add(id + ": unknown condition '" + w + "'");
                foreach (RuleSpecial s in r.Special)
                    if (s == null || string.IsNullOrEmpty(s.Kind) || !KnownSpecials.Contains(s.Kind))
                        problems.Add(id + ": unknown special case '" + (s != null ? s.Kind : "null") + "'");
                if (r.Allow != null && r.Allow.Deactivate && string.IsNullOrEmpty(r.Evidence))
                    problems.Add(id + ": deactivation without evidence (requirement 10.2: harness and benchmark)");

                if (r.Match == null) { problems.Add(id + ": no match block"); continue; }
                bool hasPath = !string.IsNullOrEmpty(r.Match.Path), hasClass = !string.IsNullOrEmpty(r.Match.Class);
                if (hasPath == hasClass) { problems.Add(id + ": exactly one of match.path and match.class"); continue; }
                if (inventory == null) continue;

                List<InventoryObject> found = Matches(r.Match, inventory);
                int expected = r.Match.Expect != null && r.Match.Expect.Count > 0 ? r.Match.Expect.Count : (hasPath ? 1 : 0);
                if (found.Count == 0) problems.Add(id + ": matches nothing in the inventory");
                else if (expected > 0 && found.Count != expected) problems.Add(id + ": matches " + found.Count + " objects, expected " + expected);
                if (r.Match.Expect == null) continue;
                foreach (InventoryObject o in found)
                {
                    foreach (string c in r.Match.Expect.Components)
                        if (!o.Components.Contains(c)) problems.Add(id + ": " + o.Path + " has no " + c);
                    foreach (string f in r.Match.Expect.Fsms)
                        if (!o.Fsms.Exists(x => x.Name == f)) problems.Add(id + ": " + o.Path + " has no FSM " + f);
                }
                foreach (string f in r.NoRestart)
                    foreach (InventoryObject o in found)
                        if (!o.Fsms.Exists(x => x.Name == f)) problems.Add(id + ": noRestart FSM " + f + " not on " + o.Path);
            }
            return problems;
        }

        public static List<InventoryObject> Matches(RuleMatch match, Inventory inventory)
        {
            List<InventoryObject> found = new List<InventoryObject>();
            if (!string.IsNullOrEmpty(match.Path))
            {
                InventoryObject o = inventory.Find(match.Path);
                if (o != null) found.Add(o);
                return found;
            }
            Glob g = new Glob(match.Class);
            foreach (InventoryObject o in inventory.Objects) if (g.IsMatch(o.Path)) found.Add(o);
            return found;
        }
    }
}
