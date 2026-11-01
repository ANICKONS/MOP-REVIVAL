// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// What the render systems share (ported from MOPR 5.x Next/RenderOptimizer.cs): object sizes, the screen size of
// one metre, and which renderers the game itself asks "am I visible?" (requirement 9.1: rendering must not change
// the game's scripts). Unity reports a renderer as visible when a camera draws it or its shadow, so a renderer whose
// visibility the game reads is never culled by distance (its layer is left alone) and keeps its shadow.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MOPR6.Render
{
    internal static class RenderScan
    {
        public const int LayerCount = 32;
        public const int UiLayer = 5;

        /// <summary>World-space diagonal of the renderer, or -1 when it cannot be known (no mesh).</summary>
        public static float Size(Renderer r)
        {
            if (r.enabled && r.gameObject.activeInHierarchy) return r.bounds.size.magnitude;
            // Inactive: the bounds are stale; the mesh bounds scaled to world space.
            Mesh mesh = null;
            SkinnedMeshRenderer skinned = r as SkinnedMeshRenderer;
            if (skinned != null) mesh = skinned.sharedMesh;
            else
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh == null) return -1f;
            return Vector3.Scale(mesh.bounds.size, r.transform.lossyScale).magnitude;
        }

        /// <summary>Pixels one metre covers at one metre from the camera (screen height and field of view).</summary>
        public static float PixelsPerMeter(Camera cam)
        {
            float fov = cam != null ? cam.fieldOfView : 60f;
            return Screen.height / (2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
        }

        /// <summary>MOPR6's own objects (merged meshes) and the original MOP's helpers.</summary>
        public static bool IsOurs(GameObject go)
        {
            string n = go.name;
            return n.StartsWith("MOPR_", StringComparison.Ordinal) || n.StartsWith("MOP_", StringComparison.Ordinal);
        }

        // ----- Renderers whose visibility the game reads ------------------------------------------------------

        static HashSet<int> observed;
        static readonly bool[] observedLayers = new bool[LayerCount];
        static string observedSummary = "not scanned";

        public static string ObservedSummary => observedSummary;

        /// <summary>A new scene: scan again on first use.</summary>
        public static void StartScene() { observed = null; }

        public static bool IsObserved(Renderer r) { EnsureObserved(); return observed.Contains(r.GetInstanceID()); }

        public static bool LayerObserved(int layer) { EnsureObserved(); return layer >= 0 && layer < LayerCount && observedLayers[layer]; }

        static void EnsureObserved()
        {
            if (observed != null) return;
            observed = new HashSet<int>();
            for (int i = 0; i < LayerCount; i++) observedLayers[i] = false;
            int sounds = 0;
            // MasterAudio's EventSounds plays a sound on OnBecameVisible when useVisibleSound is set.
            Type eventSounds = Type.GetType("EventSounds, Assembly-CSharp", false);
            FieldInfo visibleSound = eventSounds != null ? eventSounds.GetField("useVisibleSound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
            if (visibleSound != null)
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(eventSounds))
                {
                    Component c = o as Component;
                    if (c == null || !(visibleSound.GetValue(c) is bool b) || !b) continue;
                    MarkObserved(c.gameObject);
                    sounds++;
                }
            // FSMs with GameObjectIsVisible / FindClosest: their target is often a variable (vanilla: the wood
            // carrier's WoodTrigger checks {WoodMesh}, one of the carrier's own meshes - scenario visibility-grep), so
            // the whole object the FSM belongs to is marked (the owner's parent and everything under it).
            int fsms = 0;
            foreach (PlayMakerFSM pm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (pm == null || pm.Fsm == null || !ReadsVisibility(pm)) continue;
                Transform t = pm.transform.parent != null ? pm.transform.parent : pm.transform;
                MarkObserved(t.gameObject);
                fsms++;
            }
            List<string> layers = new List<string>();
            for (int i = 0; i < LayerCount; i++) if (observedLayers[i]) layers.Add(i + " " + LayerMask.LayerToName(i));
            observedSummary = observed.Count + " renderers (EventSounds " + sounds + ", FSM targets " + fsms + "), layers: " +
                              (layers.Count > 0 ? string.Join(", ", layers.ToArray()) : "none");
        }

        static void MarkObserved(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                observed.Add(r.GetInstanceID());
                int l = r.gameObject.layer;
                if (l >= 0 && l < LayerCount) observedLayers[l] = true;
            }
        }

        static readonly FieldInfo actionNamesField = typeof(HutongGames.PlayMaker.ActionData).GetField("actionNames", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>True if a state of the FSM has an action that reads a renderer's visibility (names only, nothing is loaded).</summary>
        static bool ReadsVisibility(PlayMakerFSM pm)
        {
            if (actionNamesField == null) return false;
            foreach (HutongGames.PlayMaker.FsmState s in pm.FsmStates)
            {
                if (s == null) continue;
                if (s.ActionsLoaded && s.Actions != null)
                    foreach (HutongGames.PlayMaker.FsmStateAction a in s.Actions)
                        if (a is HutongGames.PlayMaker.Actions.GameObjectIsVisible || a is HutongGames.PlayMaker.Actions.FindClosest) return true;
                if (s.ActionData == null) continue;
                List<string> names = actionNamesField.GetValue(s.ActionData) as List<string>;
                if (names == null) continue;
                foreach (string n in names)
                    if (n != null && (n.EndsWith(".GameObjectIsVisible", StringComparison.Ordinal) || n.EndsWith(".FindClosest", StringComparison.Ordinal)))
                        return true;
            }
            return false;
        }
    }
}
