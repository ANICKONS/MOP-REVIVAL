// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Version of MOPR6: "major.minor.patch" with an optional "-preview.N" suffix. Builds stay marked as previews
// until the release gate is met (requirement 1.3); the update checker compares versions with this type.
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Globalization;

namespace MOPR6.Core
{
    public struct ModVersion : IComparable<ModVersion>, IEquatable<ModVersion>
    {
        const string PreviewTag = "-preview.";

        public readonly int Major;
        public readonly int Minor;
        public readonly int Patch;
        /// <summary>Preview number; 0 = a release build.</summary>
        public readonly int Preview;

        public ModVersion(int major, int minor, int patch, int preview = 0)
        {
            if (major < 0 || minor < 0 || patch < 0 || preview < 0)
                throw new ArgumentOutOfRangeException(nameof(major), "Version parts must not be negative.");
            Major = major;
            Minor = minor;
            Patch = patch;
            Preview = preview;
        }

        public bool IsPreview => Preview > 0;

        /// <summary>Parses "6.0.0" or "6.0.0-preview.3" (an optional leading "v", as in git tags).</summary>
        public static bool TryParse(string text, out ModVersion version)
        {
            version = default(ModVersion);
            if (string.IsNullOrEmpty(text)) return false;

            string s = text.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

            int preview = 0;
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                string suffix = s.Substring(dash);
                if (!suffix.StartsWith(PreviewTag, StringComparison.Ordinal)) return false;
                if (!TryPart(suffix.Substring(PreviewTag.Length), out preview) || preview == 0) return false;
                s = s.Substring(0, dash);
            }

            string[] parts = s.Split('.');
            if (parts.Length != 3) return false;
            if (!TryPart(parts[0], out int major) || !TryPart(parts[1], out int minor) || !TryPart(parts[2], out int patch))
                return false;

            version = new ModVersion(major, minor, patch, preview);
            return true;
        }

        public static ModVersion Parse(string text)
        {
            if (!TryParse(text, out ModVersion v)) throw new FormatException("Not a MOPR version: '" + text + "'");
            return v;
        }

        // Digits only: no sign, no spaces, no leading zeros except "0" itself.
        static bool TryPart(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s) || s.Length > 9) return false;
            if (s.Length > 1 && s[0] == '0') return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            value = int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>A preview comes before the release with the same numbers: 6.0.0-preview.9 &lt; 6.0.0.</summary>
        public int CompareTo(ModVersion other)
        {
            int c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;
            if (Preview == other.Preview) return 0;
            if (Preview == 0) return 1;
            if (other.Preview == 0) return -1;
            return Preview.CompareTo(other.Preview);
        }

        public bool Equals(ModVersion other) => CompareTo(other) == 0;
        public override bool Equals(object obj) => obj is ModVersion v && Equals(v);
        public override int GetHashCode() => ((Major * 397 ^ Minor) * 397 ^ Patch) * 397 ^ Preview;

        public static bool operator ==(ModVersion a, ModVersion b) => a.Equals(b);
        public static bool operator !=(ModVersion a, ModVersion b) => !a.Equals(b);
        public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;

        public override string ToString()
        {
            string s = Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." +
                       Patch.ToString(CultureInfo.InvariantCulture);
            return IsPreview ? s + PreviewTag + Preview.ToString(CultureInfo.InvariantCulture) : s;
        }
    }
}
