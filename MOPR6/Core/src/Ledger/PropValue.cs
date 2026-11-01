// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// What the change ledger records (requirement 5, design §3): the kinds of properties the mod may change and their
// values, without Unity types (a Vector3 / Quaternion is four floats, a parent is an instance id).
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Globalization;

namespace MOPR6.Core.Ledger
{
    public enum PropKind : byte
    {
        GoActive,
        BehaviourEnabled,
        RendererEnabled,
        ColliderEnabled,
        Layer,
        RbKinematic,
        RbDetectCollisions,
        RbUseGravity,
        RbSleep,
        FsmEnabled,
        FsmRestartOnEnable,
        Parent,
        LocalPosition,
        LocalRotation,
        ShadowCasting,
        LightEnabled,
        LightShadows,
        CameraLayerCull,
        QualityShadowDistance,
        /// <summary>An object the mod created (merged meshes): "restoring" it destroys it.</summary>
        CreatedObject,
        /// <summary>Camera.layerCullSpherical (render.layers culls by distance, not by the view plane).</summary>
        CameraCullSpherical
    }

    public enum PropType : byte
    {
        None,
        Bool,
        Int,
        Float,
        Vector,      // X, Y, Z (W for quaternions)
        Ref,         // an instance id (0 = none)
        Floats       // A (Camera.layerCullDistances: 32 floats)
    }

    /// <summary>A property value. Equality is exact (bitwise for floats): the ledger compares what it wrote.</summary>
    public struct PropValue : IEquatable<PropValue>
    {
        public PropType Type;
        public int I;          // Bool (0/1), Int, Ref
        public float X, Y, Z, W;
        /// <summary>Floats only; a private copy (never the caller's array).</summary>
        public float[] A;

        public static PropValue Bool(bool b) => new PropValue { Type = PropType.Bool, I = b ? 1 : 0 };
        public static PropValue Int(int i) => new PropValue { Type = PropType.Int, I = i };
        public static PropValue Float(float f) => new PropValue { Type = PropType.Float, X = f };
        public static PropValue Vector(float x, float y, float z, float w = 0f) => new PropValue { Type = PropType.Vector, X = x, Y = y, Z = z, W = w };
        public static PropValue Ref(int instanceId) => new PropValue { Type = PropType.Ref, I = instanceId };
        public static PropValue Floats(float[] values) =>
            new PropValue { Type = PropType.Floats, A = values != null ? (float[])values.Clone() : new float[0] };

        public bool AsBool => I != 0;

        public bool Equals(PropValue o) =>
            Type == o.Type && I == o.I && Same(X, o.X) && Same(Y, o.Y) && Same(Z, o.Z) && Same(W, o.W) && SameArray(A, o.A);

        static bool Same(float a, float b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

        static bool SameArray(float[] a, float[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PropValue v && Equals(v);
        public override int GetHashCode() => (((int)Type * 397) ^ I) * 397 ^ X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() << 4) ^ (W.GetHashCode() << 6) ^ (A != null ? A.Length << 8 : 0);
        public static bool operator ==(PropValue a, PropValue b) => a.Equals(b);
        public static bool operator !=(PropValue a, PropValue b) => !a.Equals(b);

        public override string ToString()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            switch (Type)
            {
                case PropType.Bool: return AsBool ? "true" : "false";
                case PropType.Int: return I.ToString(c);
                case PropType.Float: return X.ToString("R", c);
                case PropType.Vector: return X.ToString("R", c) + "," + Y.ToString("R", c) + "," + Z.ToString("R", c) + "," + W.ToString("R", c);
                case PropType.Ref: return "#" + I.ToString(c);
                case PropType.Floats:
                    {
                        if (A == null) return "[]";
                        string[] parts = new string[A.Length];
                        for (int i = 0; i < A.Length; i++) parts[i] = A[i].ToString("0.#", c);
                        return "[" + string.Join(" ", parts) + "]";
                    }
                default: return "-";
            }
        }
    }

    /// <summary>One property of one object: instance id + kind.</summary>
    public struct PropKey : IEquatable<PropKey>
    {
        public readonly int Target;
        public readonly PropKind Kind;

        public PropKey(int target, PropKind kind)
        {
            Target = target;
            Kind = kind;
        }

        public long Packed => ((long)Target << 8) | (byte)Kind;

        public bool Equals(PropKey o) => Target == o.Target && Kind == o.Kind;
        public override bool Equals(object obj) => obj is PropKey k && Equals(k);
        public override int GetHashCode() => Target * 31 + (int)Kind;
        public override string ToString() => "#" + Target.ToString(CultureInfo.InvariantCulture) + "." + Kind;
    }
}
