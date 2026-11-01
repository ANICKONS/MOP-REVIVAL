// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The inventory of the vanilla game (requirement 2.1, design §5): every object MOPR6 may manage, with a fingerprint
// of its structure. Generated from a dump of the game without mods (TestBot "inventory"), never edited by hand. At
// load the mod compares each live object with its entry; a mod that added a component, inserted an FSM action or
// changed the children changes the fingerprint, and MOPR6 leaves that object alone (2.2).
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Text;

namespace MOPR6.Core.Catalog
{
    /// <summary>The structure of one FSM as far as it matters for "is it still vanilla".</summary>
    public sealed class FsmShape
    {
        public string Name;
        public string StartState;
        public List<StateShape> States = new List<StateShape>();
        /// <summary>Global transitions: "EVENT>State".</summary>
        public List<string> GlobalTransitions = new List<string>();
    }

    public sealed class StateShape
    {
        public string Name;
        /// <summary>Action type names in order (disabled actions with a leading '-').</summary>
        public List<string> Actions = new List<string>();
        /// <summary>"EVENT>State" in order.</summary>
        public List<string> Transitions = new List<string>();
    }

    public sealed class InventoryFsm
    {
        public string Name;
        public int Index;          // among FSMs of the same name on the object
        public string Hash;
    }

    public sealed class InventoryObject
    {
        public string Path;                                    // stable path ("name~k", as fullsnap)
        public List<string> Components = new List<string>();   // sorted type names (PlayMakerFSM counted)
        public int Children;
        public List<InventoryFsm> Fsms = new List<InventoryFsm>();

        /// <summary>Fingerprint of the object itself: components and number of children.</summary>
        public string ObjectHash() => Fingerprint.Of(string.Join(",", Components.ToArray()) + "|" + Children);
    }

    public sealed class Inventory
    {
        /// <summary>Hash of Assembly-CSharp.dll the inventory was made from (a game update changes it).</summary>
        public string GameAssemblyHash;
        public string Created;
        public List<InventoryObject> Objects = new List<InventoryObject>();

        Dictionary<string, InventoryObject> byPath;

        public InventoryObject Find(string path)
        {
            if (byPath == null)
            {
                byPath = new Dictionary<string, InventoryObject>(StringComparer.Ordinal);
                foreach (InventoryObject o in Objects) byPath[o.Path] = o;
            }
            InventoryObject r;
            return byPath.TryGetValue(path, out r) ? r : null;
        }

        public void Invalidate() { byPath = null; }
    }

    public static class Fingerprint
    {
        /// <summary>FNV-1a 64 of the UTF-8 bytes, 16 hex digits. Stable across runtimes (no GetHashCode).</summary>
        public static string Of(string text)
        {
            ulong h = 14695981039346656037UL;
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        /// <summary>Hash of an FSM's structure: name, start state, states with their actions and transitions, globals.</summary>
        public static string Of(FsmShape fsm)
        {
            if (fsm == null) throw new ArgumentNullException(nameof(fsm));
            StringBuilder sb = new StringBuilder(256);
            sb.Append(fsm.Name).Append('\n').Append(fsm.StartState).Append('\n');
            foreach (StateShape s in fsm.States)
            {
                sb.Append('[').Append(s.Name).Append(']');
                foreach (string a in s.Actions) sb.Append(a).Append(';');
                sb.Append('|');
                foreach (string t in s.Transitions) sb.Append(t).Append(';');
                sb.Append('\n');
            }
            sb.Append("globals:");
            foreach (string g in fsm.GlobalTransitions) sb.Append(g).Append(';');
            return Of(sb.ToString());
        }
    }
}
