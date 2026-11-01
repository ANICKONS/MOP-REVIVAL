// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The save barrier in the game (requirement 6, design §4). The FsmState.OnEnter prefix of MOPR6 (Engine.FsmHooks)
// passes every state the game enters; each state is classified once (cached):
//   * "Mute audio" of a save point's Button FSM (the first state after the click, before its SaveString /
//     SetIsKinematic actions)                                      -> raise the barrier
//   * any state with a SendEvent BroadcastAll SAVEGAME (save points' "Save all", death, the phone...)
//                                                                  -> raise if not raised yet (logged), then "written"
// The prefix runs before the state's actions, so the barrier is up before the first Save* action of the game.
// Raising: every system is suspended (sleepers wake, objects come back), the ledger restores everything and is
// frozen, and what did not go back is logged by name (6.2). A cancelled save (the save point back in "Wait" without
// SAVEGAME, or nothing written for 1.5 s) resumes the systems (6.4); a save that stays in the scene resumes 3 s after
// SAVEGAME; save and quit ends with the scene.

using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MOPR6.Core.Ledger;
using MOPR6.Core.Save;
using MOPR6.Diagnostics;
using MOPR6.Host;
using MOPR6.Ledger;

namespace MOPR6
{
    public static class SaveHooks
    {
        const byte Plain = 0, RaisePoint = 1, Broadcast = 2;
        public const double StayInSceneSeconds = 3;

        static readonly Dictionary<FsmState, byte> classified = new Dictionary<FsmState, byte>();
        public static readonly SaveBarrier Barrier = new SaveBarrier();
        static PlayMakerFSM raisedBy;
        static double writtenAt = -1;

        public static int Raised => Barrier.Raises;
        public static string LastSource { get; private set; }
        public static int LastNotRestored { get; private set; }

        /// <summary>A new game scene: forget the old states, the barrier is down.</summary>
        public static void StartScene()
        {
            classified.Clear();
            Barrier.Unloaded();
            raisedBy = null;
            writtenAt = -1;
        }

        /// <summary>From the FsmState.OnEnter prefix (Engine.FsmHooks), before the state's actions run.</summary>
        internal static void OnEnter(FsmState __instance)
        {
            try
            {
                byte kind;
                if (!classified.TryGetValue(__instance, out kind))
                {
                    kind = Classify(__instance);
                    classified[__instance] = kind;
                }
                if (kind == Plain) return;
                PlayMakerFSM owner = __instance.Fsm != null ? __instance.Fsm.Owner as PlayMakerFSM : null;
                if (kind == RaisePoint) Raise(Describe(owner, __instance), owner);
                else
                {
                    if (Barrier.State == BarrierState.Live)
                    {
                        ModLog.Warn("save without a known save point: " + Describe(owner, __instance) + " - the barrier goes up now");
                        Raise(Describe(owner, __instance), owner);
                    }
                    Barrier.Written();
                    writtenAt = ModLog.Now();
                }
            }
            catch (Exception ex)
            {
                // The game's save must go on whatever happens here; the error is reported once.
                ModLog.Report("save/OnEnter", ex, __instance != null ? __instance.Name : null);
            }
        }

        static byte Classify(FsmState state)
        {
            Fsm fsm = state.Fsm;
            if (fsm == null) return Plain;
            PlayMakerFSM owner = fsm.Owner as PlayMakerFSM;
            if (state.Name == "Mute audio" && fsm.Name == "Button" && owner != null && owner.gameObject.name.IndexOf("SAVEGAME", StringComparison.Ordinal) >= 0)
                return RaisePoint;
            FsmStateAction[] actions = state.Actions;
            for (int i = 0; i < actions.Length; i++)
            {
                HutongGames.PlayMaker.Actions.SendEvent send = actions[i] as HutongGames.PlayMaker.Actions.SendEvent;
                if (send == null || send.sendEvent == null || send.eventTarget == null) continue;
                if (send.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll && send.sendEvent.Name == "SAVEGAME") return Broadcast;
            }
            return Plain;
        }

        static void Raise(string source, PlayMakerFSM owner)
        {
            if (!Barrier.Raise(source, ModLog.Now())) return;
            raisedBy = owner;
            LastSource = source;
            writtenAt = -1;
            ModLog.Line("save barrier up: " + source);
            SystemHost.SuspendAll("save");
            List<RestoreRecord> odd = Arbiters.RestoreAll("save");
            LastNotRestored = odd.Count;
            if (Arbiters.Ledger != null)
            {
                Arbiters.Ledger.Freeze();
                if (Arbiters.Ledger.Count > 0) ModLog.Warn("save barrier: " + Arbiters.Ledger.Count + " changes are still held after the restore");
            }
            if (odd.Count > 0) ModLog.Warn("save barrier: " + odd.Count + " changes did not go back to the game's value (names in " + ModLog.FilePath + ")");
        }

        /// <summary>Every 0.25 s from the host: cancelled saves, saves that stay in the scene.</summary>
        public static void Tick()
        {
            if (Barrier.State == BarrierState.Live) return;
            double now = ModLog.Now();
            if (writtenAt >= 0 && now - writtenAt >= StayInSceneSeconds) Barrier.Finished();
            string state = raisedBy != null ? raisedBy.ActiveStateName : null;
            bool idle = state == "Wait" || state == "Wait for click";
            if (Barrier.Tick(now, idle) != BarrierAction.Resume) return;
            ModLog.Line("save barrier down (" + (writtenAt >= 0 ? "saved, still in the scene" : "cancelled") + ")");
            raisedBy = null;
            writtenAt = -1;
            if (Arbiters.Ledger != null) Arbiters.Ledger.Thaw();
            SystemHost.ResumeAll();
        }

        static string Describe(PlayMakerFSM owner, FsmState state)
        {
            if (owner == null) return state.Name;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(owner.gameObject.name);
            for (UnityEngine.Transform p = owner.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.Append('#').Append(owner.FsmName).Append('/').Append(state.Name).ToString();
        }
    }
}
