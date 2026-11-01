// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The arbiter for properties whose value is computed, not fixed (design §3, RenderArbiter: CameraLayerCull,
// QualityShadowDistance): one system holds a property and sets the value it computed; another system asking for the
// same property is refused (DeniedOwner) - two computed values cannot be merged. The ledger keeps the original and
// writes it back when the holder lets go. When the game changes the property (External), the holder loses it and is
// told (Dropped), so it can compute again from the game's new value.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Ledger
{
    public sealed class ValueArbiter
    {
        readonly ChangeLedger ledger;
        readonly Dictionary<long, string> holders = new Dictionary<long, string>();
        readonly Dictionary<long, PropKey> keys = new Dictionary<long, PropKey>();

        public readonly string Owner;
        public readonly PropKind Kind;

        /// <summary>The game took the property over: this system lost it.</summary>
        public event Action<PropKey, string> Dropped;

        public ValueArbiter(ChangeLedger ledger, string owner, PropKind kind)
        {
            this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("owner", nameof(owner));
            Owner = owner;
            Kind = kind;
            ledger.External += OnExternal;
        }

        public int Held => holders.Count;

        public string Holder(int target)
        {
            string s;
            return holders.TryGetValue(new PropKey(target, Kind).Packed, out s) ? s : null;
        }

        /// <summary>The value the game had before (null when nobody holds the property).</summary>
        public PropValue? Original(int target)
        {
            LedgerEntry e = ledger.Find(new PropKey(target, Kind));
            return e != null && e.Owner == Owner ? e.Original : (PropValue?)null;
        }

        /// <summary><paramref name="system"/> wants the property at <paramref name="value"/>.</summary>
        public SetResult Set(int target, string system, PropValue value)
        {
            PropKey key = new PropKey(target, Kind);
            string holder;
            if (holders.TryGetValue(key.Packed, out holder) && holder != system) return SetResult.DeniedOwner;
            SetResult r = ledger.Set(Owner, key, value);
            if (r != SetResult.Ok) return r;
            holders[key.Packed] = system;
            keys[key.Packed] = key;
            return SetResult.Ok;
        }

        /// <summary>The system lets go: the original comes back.</summary>
        public void Remove(int target, string system)
        {
            PropKey key = new PropKey(target, Kind);
            string holder;
            if (!holders.TryGetValue(key.Packed, out holder) || holder != system) return;
            holders.Remove(key.Packed);
            keys.Remove(key.Packed);
            ledger.Release(Owner, key);
        }

        public int RemoveSystem(string system)
        {
            List<long> mine = new List<long>();
            foreach (KeyValuePair<long, string> kv in holders) if (kv.Value == system) mine.Add(kv.Key);
            foreach (long k in mine)
            {
                PropKey key = keys[k];
                holders.Remove(k);
                keys.Remove(k);
                ledger.Release(Owner, key);
            }
            return mine.Count;
        }

        /// <summary>The ledger restored everything: nobody holds anything any more.</summary>
        public void Forget()
        {
            holders.Clear();
            keys.Clear();
        }

        void OnExternal(LedgerEntry e)
        {
            if (e.Owner != Owner) return;
            string holder;
            if (!holders.TryGetValue(e.Key.Packed, out holder)) return;
            holders.Remove(e.Key.Packed);
            keys.Remove(e.Key.Packed);
            ledger.Release(Owner, e.Key);   // External: the game's value stays, the entry goes
            if (Dropped != null) Dropped(e.Key, holder);
        }
    }
}
