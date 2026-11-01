// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// An arbiter is the one owner of a property kind in the ledger (design §3): systems do not write properties, they
// add and remove "reasons" ("render.layers wants this renderer hidden", "dormancy wants this FSM asleep"). While a
// property has at least one reason the arbiter holds it at Value; with the last reason gone the ledger writes the
// original back. Several systems can want the same thing without fighting over it, and two different values for
// one property are impossible by construction.
//   RenderArbiter      RendererEnabled = false, ShadowCasting / LightEnabled / LightShadows...
//   FsmControl         FsmEnabled = false (sleep), FsmRestartOnEnable = false (pause, RestartGuard)
//   PhysicsArbiter     RbKinematic = true ...
//   BehaviourArbiter   BehaviourEnabled = false, ColliderEnabled = false
// When the game changes a held property (ledger External), the arbiter drops every reason for it and tells the
// systems (Dropped), so they stop assuming their change is in place.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Ledger
{
    public sealed class ReasonArbiter
    {
        readonly ChangeLedger ledger;
        readonly Dictionary<long, HashSet<string>> reasons = new Dictionary<long, HashSet<string>>();
        readonly Dictionary<long, PropKey> keys = new Dictionary<long, PropKey>();

        public readonly string Owner;
        public readonly PropKind Kind;
        public readonly PropValue Value;

        /// <summary>The game took the property over: these systems lost their reason for it.</summary>
        public event Action<PropKey, IList<string>> Dropped;

        public ReasonArbiter(ChangeLedger ledger, string owner, PropKind kind, PropValue value)
        {
            this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("owner", nameof(owner));
            Owner = owner;
            Kind = kind;
            Value = value;
            ledger.External += OnExternal;
        }

        public int Held => reasons.Count;

        public bool Has(int target, string system)
        {
            HashSet<string> set;
            return reasons.TryGetValue(new PropKey(target, Kind).Packed, out set) && set.Contains(system);
        }

        public int ReasonCount(int target)
        {
            HashSet<string> set;
            return reasons.TryGetValue(new PropKey(target, Kind).Packed, out set) ? set.Count : 0;
        }

        /// <summary>The system wants the property at Value. Ok when it is (or already was) held.</summary>
        public SetResult Add(int target, string system)
        {
            PropKey key = new PropKey(target, Kind);
            HashSet<string> set;
            if (reasons.TryGetValue(key.Packed, out set))
            {
                set.Add(system);
                return SetResult.Ok;
            }
            SetResult r = ledger.Set(Owner, key, Value);
            if (r != SetResult.Ok) return r;
            reasons[key.Packed] = new HashSet<string>(StringComparer.Ordinal) { system };
            keys[key.Packed] = key;
            return SetResult.Ok;
        }

        /// <summary>The system no longer needs it; with the last reason gone the original comes back.</summary>
        public void Remove(int target, string system)
        {
            PropKey key = new PropKey(target, Kind);
            HashSet<string> set;
            if (!reasons.TryGetValue(key.Packed, out set) || !set.Remove(system) || set.Count > 0) return;
            reasons.Remove(key.Packed);
            keys.Remove(key.Packed);
            ledger.Release(Owner, key);
        }

        /// <summary>Every reason of one system (the system is removed or tripped).</summary>
        public int RemoveSystem(string system)
        {
            List<long> released = new List<long>();
            foreach (KeyValuePair<long, HashSet<string>> kv in reasons)
                if (kv.Value.Remove(system) && kv.Value.Count == 0) released.Add(kv.Key);
            foreach (long k in released)
            {
                PropKey key = keys[k];
                reasons.Remove(k);
                keys.Remove(k);
                ledger.Release(Owner, key);
            }
            return released.Count;
        }

        /// <summary>The ledger restored everything (save barrier, master switch): forget all reasons.</summary>
        public void Forget()
        {
            reasons.Clear();
            keys.Clear();
        }

        void OnExternal(LedgerEntry e)
        {
            if (e.Owner != Owner) return;
            HashSet<string> set;
            if (!reasons.TryGetValue(e.Key.Packed, out set)) return;
            reasons.Remove(e.Key.Packed);
            keys.Remove(e.Key.Packed);
            ledger.Release(Owner, e.Key);   // External: the game's value stays, the entry goes
            if (Dropped != null) Dropped(e.Key, new List<string>(set));
        }
    }
}
