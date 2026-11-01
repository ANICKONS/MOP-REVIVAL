// Virtual input for deterministic runs: the bot's own "keyboard and pedals". InputLock keeps the real keyboard, mouse
// and controller from the game; with VirtualInput.Active the same patched readers of cInput (the game's key binding
// layer: GetKey*, GetButton*, GetAxis, GetAxisRaw) return the values set here instead of "nothing pressed". The car
// controller, the pedal and gearbox FSMs then see exactly what they would see from a player (AutoDriver.cs).
//
// Timing: values are set at the end of a frame (AutoDriver works in a WaitForEndOfFrame coroutine) and apply to the
// whole next frame, so every reader of a frame sees the same value whatever the script order:
//   * axes keep their value until set again;
//   * Press(name, frames) holds a key for that many frames starting with the next one: GetKey is true for those
//     frames, GetKeyDown on the first of them, GetKeyUp on the frame after the last.
// cInput finds axes and keys by string.GetHashCode() of their name; the int overloads use the same hash.

using System.Collections.Generic;
using UnityEngine;

namespace MOPR.TestBot
{
    static class VirtualInput
    {
        struct Key { public int Start, End; }   // held for Start <= frame < End

        /// <summary>The readers consult the tables below (stays on for the rest of the run once a driver used it).</summary>
        public static bool Active;

        static readonly Dictionary<int, float> axes = new Dictionary<int, float>();
        static readonly Dictionary<int, Key> keys = new Dictionary<int, Key>();
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();

        public static void SetAxis(string name, float value)
        {
            if (string.IsNullOrEmpty(name)) return;
            int h = name.GetHashCode();
            names[h] = name;
            axes[h] = value;
        }

        /// <summary>Holds the key for <paramref name="frames"/> frames from the next frame on.</summary>
        public static void Press(string name, int frames)
        {
            if (string.IsNullOrEmpty(name)) return;
            int h = name.GetHashCode();
            names[h] = name;
            int start = Time.frameCount + 1;
            keys[h] = new Key { Start = start, End = start + Mathf.Max(1, frames) };
        }

        /// <summary>Holds the key from the next frame until Hold(name, false).</summary>
        public static void Hold(string name, bool down)
        {
            if (string.IsNullOrEmpty(name)) return;
            int h = name.GetHashCode();
            names[h] = name;
            int next = Time.frameCount + 1;
            Key k;
            bool held = keys.TryGetValue(h, out k) && k.Start <= next && next < k.End;
            if (down && !held) keys[h] = new Key { Start = next, End = int.MaxValue };
            else if (!down && held) keys[h] = new Key { Start = k.Start, End = next };
        }

        /// <summary>Every axis back to 0 and every key up (from the next frame).</summary>
        public static void ReleaseAll()
        {
            List<int> held = new List<int>(axes.Keys);
            foreach (int h in held) axes[h] = 0f;
            foreach (int h in new List<int>(keys.Keys))
            {
                Key k = keys[h];
                if (k.End > Time.frameCount + 1) keys[h] = new Key { Start = k.Start, End = Mathf.Max(k.Start, Time.frameCount + 1) };
            }
        }

        public static bool Axis(int hash, out float value) => axes.TryGetValue(hash, out value);

        public static bool KeyHeld(int hash)
        {
            Key k;
            int f = Time.frameCount;
            return keys.TryGetValue(hash, out k) && k.Start <= f && f < k.End;
        }

        public static bool KeyDown(int hash)
        {
            Key k;
            return keys.TryGetValue(hash, out k) && k.Start == Time.frameCount;
        }

        public static bool KeyUp(int hash)
        {
            Key k;
            return keys.TryGetValue(hash, out k) && k.End == Time.frameCount;
        }

        public static string Describe()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, float> a in axes)
                if (a.Value != 0f) parts.Add(names[a.Key] + "=" + a.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            foreach (KeyValuePair<int, Key> k in keys)
                if (KeyHeld(k.Key)) parts.Add("[" + names[k.Key] + "]");
            return parts.Count == 0 ? "-" : string.Join(" ", parts.ToArray());
        }
    }
}
