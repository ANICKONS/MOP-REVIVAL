// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Layout tweaks of the settings page that MSCLoader's API cannot express (5.1, ported from MOPR 4.x):
// two buttons side by side, and a dropdown without its (empty) label row.
//
// MSCLoader creates the UI elements only when the settings page opens, and rebuilds them on every open and
// after "Reset settings". So the tweaks are re-applied a few times per second; each one is a quick no-op when
// the page is closed (no element) or when it is already applied. Everything is guarded: if MSCLoader changes
// its internals, the page simply keeps its normal layout.
//
// MSCLoader members used (1.4.2): ModSetting.SettingsElement (internal field), SettingsElement.settingName.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MSCLoader;
using UnityEngine;
using UnityEngine.UI;

namespace MOP.Common
{
    internal sealed class SettingsUiPatcher : MonoBehaviour
    {
        const string RowName = "MOPR_ButtonRow";
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo elementField = typeof(ModSetting).GetField("SettingsElement", Any);

        static SettingsUiPatcher instance;
        readonly List<Func<bool>> patches = new List<Func<bool>>();

        static SettingsUiPatcher Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject host = new GameObject("MOPR_SettingsUiPatcher");
                    DontDestroyOnLoad(host);
                    instance = host.AddComponent<SettingsUiPatcher>();
                    instance.StartCoroutine(instance.Loop());
                }
                return instance;
            }
        }

        /// <summary>Forgets every tweak (called before the settings page is built).</summary>
        internal static void Clear()
        {
            if (instance != null) instance.patches.Clear();
        }

        /// <summary>Hides the label row of a setting (the language dropdown has no label).</summary>
        internal static void HideLabel(ModSetting setting)
        {
            if (setting != null) Instance.patches.Add(() => ApplyHideLabel(setting));
        }

        /// <summary>Shows two buttons on one row, half the width each.</summary>
        internal static void PairButtons(ModSetting a, ModSetting b)
        {
            if (a != null && b != null) Instance.patches.Add(() => ApplyPair(a, b));
        }

        IEnumerator Loop()
        {
            WaitForSeconds wait = new WaitForSeconds(0.4f);
            while (true)
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    try { patches[i](); }
                    catch { }
                }
                yield return wait;
            }
        }

        static Component Element(ModSetting setting)
        {
            return elementField != null ? elementField.GetValue(setting) as Component : null;
        }

        static bool ApplyHideLabel(ModSetting setting)
        {
            Component element = Element(setting);
            if (element == null) return false;
            FieldInfo f = element.GetType().GetField("settingName", Any);
            Text label = f != null ? f.GetValue(element) as Text : null;
            if (label == null || !label.gameObject.activeSelf) return label != null;

            GameObject go = label.gameObject;
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, 0f);
            LayoutElement le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = 0f;
            le.preferredHeight = 0f;
            go.SetActive(false);
            return true;
        }

        static bool ApplyPair(ModSetting a, ModSetting b)
        {
            Component ea = Element(a), eb = Element(b);
            if (ea == null || eb == null) return false;
            RectTransform ra = ea.GetComponent<RectTransform>(), rb = eb.GetComponent<RectTransform>();
            if (ra == null || rb == null) return false;
            if (ra.parent != null && ra.parent.name == RowName) return true;   // already paired

            Transform content = ra.parent;
            if (content == null) return false;

            float height = Mathf.Max(ra.rect.height, 30f);
            int index = ra.GetSiblingIndex();

            GameObject rowObject = new GameObject(RowName, new Type[] { typeof(RectTransform) });
            RectTransform row = (RectTransform)rowObject.transform;
            row.SetParent(content, false);
            row.SetSiblingIndex(index);

            HorizontalLayoutGroup layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.spacing = 6f;

            LayoutElement le = rowObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            ra.SetParent(row, false);
            rb.SetParent(row, false);
            return true;
        }
    }
}
