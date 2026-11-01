// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// A test-only system for the harness (tasks 18, 19, 21): it does nothing for performance. It hides the renderers
// under one object through the render arbiter (ledger, restore, save barrier), and on request throws from its tick
// (failure isolation). Never installed unless the console / the TestBot asks: "mopr probe hide <path>",
// "mopr probe throw", "mopr probe off".

using System;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Host;
using MOPR6.Ledger;
using UnityEngine;

namespace MOPR6.Systems
{
    public sealed class ProbeSystem : Optimization
    {
        public static readonly ProbeSystem Instance = new ProbeSystem();
        public const string SystemId = "test.probe";

        public string HidePath;
        public bool ThrowOnTick;
        int hidden, ticks, resumes;

        ProbeSystem() : base(SystemId) { }

        public override string Title => "Test probe";
        public override Tier Tier => Tier.Test;

        protected internal override string OnInstall(SystemMode mode)
        {
            hidden = 0;
            return null;
        }

        protected internal override void OnRemove() { hidden = 0; }

        protected internal override void OnSuspend(string reason) { hidden = 0; }

        protected internal override void OnResume() { resumes++; }

        protected internal override void OnTick(double now)
        {
            ticks++;
            if (ThrowOnTick) throw new InvalidOperationException("probe: requested failure (tick " + ticks + ")");
            if (hidden > 0 || string.IsNullOrEmpty(HidePath)) return;
            GameObject root = GameObject.Find(HidePath);
            if (root == null) return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                if (Arbiters.Render.Add(Arbiters.Id(r), Id) == SetResult.Ok) hidden++;
        }

        public override void Report(StringBuilder sb)
        {
            sb.Append("  probe: hide '").Append(HidePath ?? "").Append("', hidden ").Append(hidden).Append(", ticks ").Append(ticks)
              .Append(", resumes ").Append(resumes).Append(ThrowOnTick ? ", throwing" : "").Append('\n');
        }
    }
}
