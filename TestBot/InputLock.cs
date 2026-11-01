// Input lock for deterministic runs (MOPR6 equivalence harness): while On, the game gets no player input from the
// keyboard, the mouse or a controller, so a person using the PC during a run (the game window takes the focus, a key
// or a mouse move reaches it) cannot turn the camera, walk or click and make the run differ from the others.
// It works on the readers of input, since UnityEngine.Input itself is native and cannot be patched:
//   * cInput (the game's key binding layer): GetKey*, GetButton* -> false, GetAxis / GetAxisRaw -> 0 - or, with
//     VirtualInput.Active, the bot's own keys and axes (VirtualInput.cs; the AutoDriver drives the car through them);
//   * the components that turn and move the player: MouseLook, SmoothMouseLook, SimpleSmoothMouseLook,
//     FPSInputController, TrackMouseMovement (Update skipped: no rotation / movement from input);
//   * the PlayMaker actions that read input (AnyKey, GetKey*, GetButton*, GetMouseButton*, GetAxis*, GetMouseX/Y,
//     MouseLook*, AxisEvent; OnUpdate skipped: they neither send an event nor store a value; the ones that read cInput
//     - AxisEvent, GetButton* - run while the virtual input is active);
//   * the click messages of PlayMakerMouseEvents (OnMouseDown / Up / UpAsButton / Drag). Hover messages (Enter, Over,
//     Exit) stay: they follow the camera, not the input.
// The same lock is on in every variant of a comparison (vanilla and the mod), so it changes nothing between them.
// Not for normal scenarios: the bot's own steps do not need input, but a person watching cannot play either.

using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;

namespace MOPR.TestBot
{
    static class InputLock
    {
        const string HarmonyId = "mopr.testbot.inputlock";
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static HarmonyInstance harmony;
        public static bool On;
        public static int Patched;
        public static readonly List<string> Missing = new List<string>();

        static readonly string[] Components = { "MouseLook", "SmoothMouseLook", "SimpleSmoothMouseLook", "FPSInputController", "TrackMouseMovement" };
        static readonly string[] Actions =
        {
            "AnyKey", "AnyKeyStoreString", "AxisEvent", "GetAxis", "GetAxisVector", "GetButton", "GetButtonDown", "GetButtonUp",
            "GetKey", "GetKeyDown", "GetKeyUp", "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseX", "GetMouseY",
            "MouseLook", "MouseLook2", "MouseLook3"
        };
        static readonly string[] MouseClicks = { "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseDrag" };
        /// <summary>PlayMaker actions of the list above that read cInput, not UnityEngine.Input (decompiled game).</summary>
        static readonly string[] CInputActions = { "AxisEvent", "GetButton", "GetButtonDown", "GetButtonUp" };

        /// <summary>Patches once per process (the patches stay; On switches them). Returns a summary.</summary>
        public static string Install()
        {
            if (harmony != null) return Summary();
            harmony = HarmonyInstance.Create(HarmonyId);
            MethodInfo skip = Hook(nameof(SkipPrefix)), no = Hook(nameof(FalsePrefix)), zero = Hook(nameof(ZeroPrefix));

            Type ci = FindType("cInput");
            if (ci == null) Missing.Add("cInput");
            else
                foreach (MethodInfo m in ci.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    bool keyLike = m.Name.StartsWith("GetKey", StringComparison.Ordinal) || m.Name.StartsWith("GetButton", StringComparison.Ordinal);
                    bool axis = (m.Name == "GetAxis" || m.Name == "GetAxisRaw") && m.ReturnType == typeof(float);
                    if (!(keyLike && m.ReturnType == typeof(bool)) && !axis) continue;
                    // The bot's virtual input (VirtualInput.cs) answers through the same patch: by the name or its hash.
                    ParameterInfo[] ps = m.GetParameters();
                    string kind = axis ? "Axis" : m.Name.EndsWith("Down", StringComparison.Ordinal) ? "Down" : m.Name.EndsWith("Up", StringComparison.Ordinal) ? "Up" : "Held";
                    string suffix = ps.Length == 1 && ps[0].ParameterType == typeof(string) && ps[0].Name == "description" ? "S"
                                  : ps.Length == 1 && ps[0].ParameterType == typeof(int) && ps[0].Name == "descriptionHash" ? "I" : null;
                    if (suffix == null) Patch(m, axis ? zero : no);
                    else Patch(m, Hook("Virtual" + kind + suffix));
                }

            foreach (string n in Components)
            {
                Type t = FindType(n);
                MethodInfo u = t?.GetMethod("Update", Any, null, Type.EmptyTypes, null);
                if (u == null) { Missing.Add(n + ".Update"); continue; }
                Patch(u, skip);
            }
            foreach (string n in Actions)
            {
                Type t = FindType("HutongGames.PlayMaker.Actions." + n);
                MethodInfo u = t?.GetMethod("OnUpdate", Any, null, Type.EmptyTypes, null);
                if (u == null) { Missing.Add(n + ".OnUpdate"); continue; }
                // These read cInput (patched above): with the virtual input on they run and see the bot's keys.
                Patch(u, Array.IndexOf(CInputActions, n) >= 0 ? Hook(nameof(SkipUnlessVirtual)) : skip);
            }
            Type pme = FindType("PlayMakerMouseEvents");
            foreach (string n in MouseClicks)
            {
                MethodInfo u = pme?.GetMethod(n, Any, null, Type.EmptyTypes, null);
                if (u == null) { Missing.Add("PlayMakerMouseEvents." + n); continue; }
                Patch(u, skip);
            }
            return Summary();
        }

        public static string Summary() => $"{Patched} input readers patched" + (Missing.Count > 0 ? ", not found: " + string.Join(", ", Missing.ToArray()) : "");

        static void Patch(MethodInfo target, MethodInfo prefix)
        {
            harmony.Patch(target, new HarmonyMethod(prefix), null, null);
            Patched++;
        }

        static MethodInfo Hook(string name) => typeof(InputLock).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType(fullName, false); }
                catch (Exception) { continue; }   // dynamic / broken assemblies of other mods: not ours to read
                if (t != null) return t;
            }
            return null;
        }

        static bool SkipPrefix() => !On;

        static bool SkipUnlessVirtual() => !On || VirtualInput.Active;

        // cInput readers with the virtual input: the bot's value, else "nothing pressed" (the real input never).
        static bool VirtualHeldS(string description, ref bool __result) => Key(description.GetHashCode(), 0, ref __result);
        static bool VirtualHeldI(int descriptionHash, ref bool __result) => Key(descriptionHash, 0, ref __result);
        static bool VirtualDownS(string description, ref bool __result) => Key(description.GetHashCode(), 1, ref __result);
        static bool VirtualDownI(int descriptionHash, ref bool __result) => Key(descriptionHash, 1, ref __result);
        static bool VirtualUpS(string description, ref bool __result) => Key(description.GetHashCode(), 2, ref __result);
        static bool VirtualUpI(int descriptionHash, ref bool __result) => Key(descriptionHash, 2, ref __result);
        static bool VirtualAxisS(string description, ref float __result) => Axis(description.GetHashCode(), ref __result);
        static bool VirtualAxisI(int descriptionHash, ref float __result) => Axis(descriptionHash, ref __result);

        static bool Key(int hash, int kind, ref bool result)
        {
            if (!On) return true;
            result = VirtualInput.Active && (kind == 0 ? VirtualInput.KeyHeld(hash) : kind == 1 ? VirtualInput.KeyDown(hash) : VirtualInput.KeyUp(hash));
            return false;
        }

        static bool Axis(int hash, ref float result)
        {
            if (!On) return true;
            float v;
            result = VirtualInput.Active && VirtualInput.Axis(hash, out v) ? v : 0f;
            return false;
        }

        static bool FalsePrefix(ref bool __result)
        {
            if (!On) return true;
            __result = false;
            return false;
        }

        static bool ZeroPrefix(ref float __result)
        {
            if (!On) return true;
            __result = 0f;
            return false;
        }
    }
}
