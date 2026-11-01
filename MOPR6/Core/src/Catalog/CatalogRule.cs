// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// A rule of the catalogue (requirement 11, design §5): what MOPR6 may do with one object or one class of objects,
// why (source) and which harness scenario proves it (test). Rules are data (embedded JSON); the conditions ("when")
// and special cases ("special") they name are implemented in code and only referenced by name.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System.Collections.Generic;

namespace MOPR6.Core.Catalog
{
    public sealed class CatalogRule
    {
        public string Id;
        public RuleMatch Match;
        public string Chunk;
        public RuleAllow Allow;
        public RuleDistance Distance;
        public List<string> When = new List<string>();
        public List<string> NoRestart = new List<string>();
        public List<RuleSpecial> Special = new List<RuleSpecial>();
        public string Source;
        public string Test;
        /// <summary>Measured proof of a decision (world chunks: "bench 2026-11-02: M1->M2 -0.31 ms, eq-full ok").</summary>
        public string Evidence;
    }

    public sealed class RuleMatch
    {
        /// <summary>One object by its stable path. Exactly one of Path and Class.</summary>
        public string Path;
        /// <summary>A class of objects: a path pattern ('*', '?'), e.g. "SATSUMA(557kg, 248)/*/BoltPM".</summary>
        public string Class;
        public RuleExpect Expect;
    }

    public sealed class RuleExpect
    {
        /// <summary>How many objects the rule must find in the inventory (0 = not checked).</summary>
        public int Count;
        /// <summary>Component type names the object must have (others may exist; the inventory hash guards the rest).</summary>
        public List<string> Components = new List<string>();
        public List<string> Fsms = new List<string>();
    }

    public sealed class RuleAllow
    {
        public bool Render;
        public bool Sleep;
        public List<string> Behaviours = new List<string>();
        public bool Physics;
        public bool Deactivate;
    }

    public sealed class RuleDistance
    {
        public float? Render;
        public float? Behaviours;
        public float? Physics;
        public float? Deactivate;
    }

    public sealed class RuleSpecial
    {
        public string Kind;
        public string Fsm;
        public string State;
        public string Value;
    }

    /// <summary>One file of rules (Resources/catalog/rules/*.json).</summary>
    public sealed class RuleFile
    {
        public string Name;
        public List<CatalogRule> Rules = new List<CatalogRule>();
    }
}
