// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Rules (5.1): what one rule source says. A source is a JSON file (built into the DLL, or the user's own in
// Mod Settings/MOPR/Rules) or an old .mopconfig / Custom.txt file (LegacyRules). Every rule has one meaning:
//
//   exclude        MOP never touches the object.
//   keepActive     MOP never switches the object off (its scripts keep running). It may still hide a world
//                  object's renderers or freeze an item's or a vehicle's physics. Old: "ignore: X".
//   keepActiveIn   children that stay on while MOP switches their place or vehicle off. Old: "ignore: Place X".
//   manage         objects of a mod that MOP should switch by distance. Old: "toggle: X [mode]".
//   reparent       move an object under another parent (null = scene root) at load. Old: "change_parent".
//   noLod          no simplified far-away copy of this vehicle. Old: "no_lod".
//   sectors        extra indoor sectors. Old: "sector".
//   satsuma        "keepRenderers" (MOP never hides the car's parts) or "exclude" (MOP leaves the car alone).
//
// Pure data: no Unity, no MSCLoader (tools/RulesCheck compiles these files too).

using System.Collections.Generic;

namespace MOP.Rules
{
    enum RuleSource { Bundled, User, Legacy }

    enum ManageKind { Objects, Renderers, Items, Vehicles, VehiclePhysics }

    enum SatsumaRule { None, KeepRenderers, Exclude }

    class ManageRule
    {
        public string Name;
        public ManageKind Kind;
    }

    class ChildRule
    {
        public string Parent;
        public string Name;
    }

    class ReparentRule
    {
        public string Name;
        public string Parent;   // null = scene root
    }

    class SectorRule
    {
        public float[] Position = new float[3];
        public float[] Size = new float[3];
        public float[] Rotation = new float[3];
        public List<string> KeepVisible = new List<string>();
        public int DrawDistance = DefaultDrawDistance;

        // MOP 3.10 created every rule sector with this draw distance.
        public const int DefaultDrawDistance = 300;
    }

    class RuleIssue
    {
        public string File;
        public int Line;            // 0 = the whole file
        public string Message;
        public bool IsError;        // false: a note (the rule still works)

        public override string ToString() => File + (Line > 0 ? ":" + Line : "") + " - " + Message;
    }

    class RuleSet
    {
        public string Name;             // file or resource name, for messages
        public string Path;             // full path of a user or legacy file
        public RuleSource Source;
        public string Mod;              // MSCLoader mod ID the rules are for; null = always active
        public bool ReplaceBundled;     // user files: the built-in rules for Mod are not used
        public bool Unreadable;         // the file could not be read at all (JSON syntax error, I/O error)

        public readonly List<string> Exclude = new List<string>();
        public readonly List<string> KeepActive = new List<string>();
        public readonly List<ChildRule> KeepActiveIn = new List<ChildRule>();
        public readonly List<ManageRule> Manage = new List<ManageRule>();
        public readonly List<ReparentRule> Reparent = new List<ReparentRule>();
        public readonly List<string> NoLod = new List<string>();
        public readonly List<SectorRule> Sectors = new List<SectorRule>();
        public SatsumaRule Satsuma;
        public bool KeepEmptyBottles;
        public bool SkipFuryColliderFix;
        // Whole-game switches: only in the user's own files without "mod" (MOP 3.10: Custom.txt only).
        public bool AllVehiclesPhysicsOnly;
        public bool NoLods;

        public readonly List<RuleIssue> Issues = new List<RuleIssue>();

        /// <summary>Why the set is not used (null = active). Set by RulesManager.</summary>
        public string InactiveReason;

        public int RuleCount =>
            Exclude.Count + KeepActive.Count + KeepActiveIn.Count + Manage.Count + Reparent.Count + NoLod.Count + Sectors.Count +
            (Satsuma != SatsumaRule.None ? 1 : 0) + (KeepEmptyBottles ? 1 : 0) + (SkipFuryColliderFix ? 1 : 0) +
            (AllVehiclesPhysicsOnly ? 1 : 0) + (NoLods ? 1 : 0);

        public int ErrorCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Issues.Count; i++) if (Issues[i].IsError) n++;
                return n;
            }
        }

        internal void Error(int line, string message) => Issues.Add(new RuleIssue { File = Name, Line = line, Message = message, IsError = true });
        internal void Note(int line, string message) => Issues.Add(new RuleIssue { File = Name, Line = line, Message = message, IsError = false });

        // Objects MOP itself depends on: a rule for them would break MOP (MOP 3.10 rejected the whole file).
        internal static readonly string[] ProtectedNames = { "MOP", "MSCLoader", "MSCUnloader", "Steam", "PLAYER", "cObject", "FPSCamera" };

        internal static bool IsProtected(string name)
        {
            for (int i = 0; i < ProtectedNames.Length; i++)
                if (ProtectedNames[i] == name) return true;
            return false;
        }

        internal static void AddUnique(List<string> list, string name)
        {
            if (!list.Contains(name)) list.Add(name);
        }
    }
}
