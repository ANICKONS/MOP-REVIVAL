// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Fast readers for private PlayMaker 1.6 fields, emitted as DynamicMethods (the same mechanism Harmony uses).
// Each reader returns several fields in one call, so the per-frame cost stays at two delegate invocations
// per FSM. Field names and types were verified against the decompiled PlayMaker.dll shipped with the game;
// Init() checks all of them and refuses to run (engine disabled) if anything does not match.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HutongGames.PlayMaker;

namespace MOP.Next.Engine
{
    /// <summary>Returns bit 0 = activeStateEntered.</summary>
    delegate int ReadFsmFn(Fsm fsm, out FsmState activeState, out FsmState switchToState);

    /// <summary>Returns bit 0 = finished, bit 1 = active.</summary>
    delegate int ReadStateFn(FsmState state, out Fsm fsm, out List<FsmStateAction> activeActions);

    /// <summary>Exactly <c>state.StateTime += delta</c> (same IL as FsmState.OnUpdate).</summary>
    delegate void AddStateTimeFn(FsmState state, float delta);

    static class FsmAccess
    {
        public const int EnteredBit = 1;
        public const int FinishedBit = 1;
        public const int ActiveBit = 2;

        public static ReadFsmFn ReadFsm;
        public static ReadStateFn ReadState;
        public static AddStateTimeFn AddStateTime;

        public static string Error { get; private set; }
        public static bool Ready => ReadFsm != null && ReadState != null && AddStateTime != null;

        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static bool Init()
        {
            if (Ready) return true;
            try
            {
                FieldInfo fsmActiveState = Field(typeof(Fsm), "activeState", typeof(FsmState));
                FieldInfo fsmSwitchTo = Field(typeof(Fsm), "switchToState", typeof(FsmState));
                FieldInfo fsmEntered = Field(typeof(Fsm), "activeStateEntered", typeof(bool));

                FieldInfo stateFsm = Field(typeof(FsmState), "fsm", typeof(Fsm));
                FieldInfo stateActions = Field(typeof(FsmState), "activeActions", typeof(List<FsmStateAction>));
                FieldInfo stateFinished = Field(typeof(FsmState), "finished", typeof(bool));
                FieldInfo stateActive = Field(typeof(FsmState), "active", typeof(bool));

                PropertyInfo stateTime = typeof(FsmState).GetProperty("StateTime", Inst);
                MethodInfo getTime = stateTime?.GetGetMethod(true);
                MethodInfo setTime = stateTime?.GetSetMethod(true);
                if (getTime == null || setTime == null || stateTime.PropertyType != typeof(float))
                    throw new MissingMemberException("FsmState.StateTime");

                Type stateRef = typeof(FsmState).MakeByRefType();

                // int ReadFsm(Fsm f, out FsmState active, out FsmState switchTo)
                DynamicMethod readFsm = new DynamicMethod("MOPR_ReadFsm", typeof(int),
                    new[] { typeof(Fsm), stateRef, stateRef }, typeof(Fsm), true);
                ILGenerator il = readFsm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fsmActiveState); il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fsmSwitchTo); il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, fsmEntered);
                il.Emit(OpCodes.Ret);

                // int ReadState(FsmState s, out Fsm fsm, out List<FsmStateAction> actions)
                DynamicMethod readState = new DynamicMethod("MOPR_ReadState", typeof(int),
                    new[] { typeof(FsmState), typeof(Fsm).MakeByRefType(), typeof(List<FsmStateAction>).MakeByRefType() }, typeof(FsmState), true);
                il = readState.GetILGenerator();
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, stateFsm); il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, stateActions); il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, stateFinished);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, stateActive);
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Ret);

                // void AddStateTime(FsmState s, float delta) => s.StateTime = s.StateTime + delta
                DynamicMethod addTime = new DynamicMethod("MOPR_AddStateTime", typeof(void),
                    new[] { typeof(FsmState), typeof(float) }, typeof(FsmState), true);
                il = addTime.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getTime);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Call, setTime);
                il.Emit(OpCodes.Ret);

                ReadFsmFn rf = (ReadFsmFn)readFsm.CreateDelegate(typeof(ReadFsmFn));
                ReadStateFn rs = (ReadStateFn)readState.CreateDelegate(typeof(ReadStateFn));
                AddStateTimeFn at = (AddStateTimeFn)addTime.CreateDelegate(typeof(AddStateTimeFn));

                SelfTest(rf, rs, at);

                ReadFsm = rf;
                ReadState = rs;
                AddStateTime = at;
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + ": " + ex.Message;
                ReadFsm = null;
                ReadState = null;
                AddStateTime = null;
                return false;
            }
        }

        static FieldInfo Field(Type owner, string name, Type expected)
        {
            FieldInfo f = owner.GetField(name, Inst);
            if (f == null || f.FieldType != expected || f.IsStatic)
                throw new MissingFieldException(owner.Name, name);
            return f;
        }

        /// <summary>Runs the emitted readers on a detached FSM and compares them with the public API.</summary>
        static void SelfTest(ReadFsmFn rf, ReadStateFn rs, AddStateTimeFn at)
        {
            Fsm fsm = new Fsm();
            FsmState state = new FsmState(fsm);

            int fl = rf(fsm, out FsmState active, out FsmState switchTo);
            if (fl != 0 || active != null || switchTo != null)
                throw new InvalidOperationException("ReadFsm self-test failed");

            int sl = rs(state, out Fsm owner, out List<FsmStateAction> _);
            if (!ReferenceEquals(owner, fsm) || (sl & FinishedBit) != 0 || ((sl & ActiveBit) != 0) != state.Active)
                throw new InvalidOperationException("ReadState self-test failed");

            float before = state.StateTime;
            at(state, 0.25f);
            if (state.StateTime != before + 0.25f)
                throw new InvalidOperationException("AddStateTime self-test failed");
        }
    }
}
