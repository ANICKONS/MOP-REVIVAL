// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The value types of Easy Save 2 as the game's ES2.dll identifies them: by ES2Type.hash (ES2Type.GetHash of
// Type.ToString(), written by ES2Writer.WriteHeader) or, in old files, by the one-byte ES2Type.key.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System.Collections.Generic;

namespace MOPR6.Core.Es2
{
    public enum Es2Type : byte
    {
        Unknown,
        Byte, SByte, Short, UShort, Int, UInt, Long, ULong, Float, Double, Decimal, Bool, Char, String, Enum,
        Vector2, Vector3, Vector4, Quaternion, Color, Color32, Rect, Bounds, Matrix4x4, Transform,
        // Known, but kept raw: their layout depends on settings or creates Unity objects.
        Mesh, Texture2D, Material, BoxCollider, SphereCollider, CapsuleCollider, MeshCollider, SkinnedMeshRenderer,
        BoneWeight
    }

    public static class Es2Types
    {
        static readonly Dictionary<int, Es2Type> byHash = new Dictionary<int, Es2Type>();
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static readonly Es2Type[] byKey = new Es2Type[81];

        static Es2Types()
        {
            Add("System.Byte", Es2Type.Byte, 0);
            Add("System.String", Es2Type.String, 1);
            Add("System.Int32", Es2Type.Int, 2);
            Add("System.UInt32", Es2Type.UInt, 3);
            Add("System.Int16", Es2Type.Short, 4);
            Add("System.UInt16", Es2Type.UShort, 5);
            Add("System.Single", Es2Type.Float, 6);
            Add("System.Int64", Es2Type.Long, 7);
            Add("System.Double", Es2Type.Double, 8);
            Add("System.Boolean", Es2Type.Bool, 9);
            Add("UnityEngine.Vector2", Es2Type.Vector2, 10);
            Add("UnityEngine.Vector3", Es2Type.Vector3, 11);
            Add("UnityEngine.Vector4", Es2Type.Vector4, 12);
            Add("UnityEngine.Color", Es2Type.Color, 13);
            Add("UnityEngine.Quaternion", Es2Type.Quaternion, 14);
            Add("UnityEngine.Mesh", Es2Type.Mesh, 15);
            Add("UnityEngine.Transform", Es2Type.Transform, 16);
            Add("UnityEngine.Texture2D", Es2Type.Texture2D, 17);
            Add("System.Char", Es2Type.Char, 19);
            Add("System.UInt64", Es2Type.ULong, 20);
            Add("UnityEngine.SphereCollider", Es2Type.SphereCollider, 21);
            Add("UnityEngine.BoxCollider", Es2Type.BoxCollider, 22);
            Add("UnityEngine.CapsuleCollider", Es2Type.CapsuleCollider, 23);
            Add("UnityEngine.MeshCollider", Es2Type.MeshCollider, 24);
            Add("UnityEngine.Color32", Es2Type.Color32, 26);
            Add("UnityEngine.Material", Es2Type.Material, 27);
            Add("UnityEngine.Rect", Es2Type.Rect, 29);
            Add("UnityEngine.Bounds", Es2Type.Bounds, 30);
            Add("UnityEngine.BoneWeight", Es2Type.BoneWeight, 33);
            // Keys 32 and 34 are used by several types in this ES2 build (Enum, Matrix4x4, sbyte; decimal,
            // SkinnedMeshRenderer): which one the reader picks depends on registration order - not mapped by key.
            Add("System.Enum", Es2Type.Enum, -1);
            Add("UnityEngine.Matrix4x4", Es2Type.Matrix4x4, -1);
            Add("System.SByte", Es2Type.SByte, -1);
            Add("System.Decimal", Es2Type.Decimal, -1);
            Add("UnityEngine.SkinnedMeshRenderer", Es2Type.SkinnedMeshRenderer, -1);
        }

        static void Add(string typeName, Es2Type type, int key)
        {
            int hash = Hash(typeName);
            byHash[hash] = type;
            names[hash] = typeName;
            if (key >= 0) byKey[key] = type;
        }

        public static Es2Type FromHash(int hash)
        {
            Es2Type t;
            return byHash.TryGetValue(hash, out t) ? t : Es2Type.Unknown;
        }

        public static Es2Type FromKey(byte key) => key < byKey.Length ? byKey[key] : Es2Type.Unknown;

        /// <summary>Type name of a known hash, or null.</summary>
        public static string NameOf(int hash)
        {
            string n;
            return names.TryGetValue(hash, out n) ? n : null;
        }

        /// <summary>Types whose values <see cref="Es2Parser"/> decodes (the rest is compared as raw bytes).</summary>
        public static bool IsDecodable(Es2Type t) => t != Es2Type.Unknown && t < Es2Type.Mesh;

        /// <summary>ES2Type.GetHash (decompiled ES2.dll), applied to a type name.</summary>
        public static int Hash(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int length = text.Length;
            uint num = (uint)length;
            int odd = length & 1;
            length >>= 1;
            int i = 0;
            unchecked
            {
                while (length > 0)
                {
                    num += text[i];
                    uint tmp = ((uint)text[i + 1] << 11) ^ num;
                    num = (num << 16) ^ tmp;
                    i += 2;
                    num += num >> 11;
                    length--;
                }
                if (odd == 1)
                {
                    num += text[i];
                    num ^= num << 11;
                    num += num >> 17;
                }
                num ^= num << 3;
                num += num >> 5;
                num ^= num << 4;
                num += num >> 17;
                num ^= num << 25;
                return (int)(num + (num >> 6));
            }
        }
    }
}
