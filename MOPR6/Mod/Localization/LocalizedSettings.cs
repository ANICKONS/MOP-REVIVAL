// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Live relabeling of MSCLoader settings after a language switch (copied from MOPR 5.x Localization/LocalizedSettings.cs).
//
// MSCLoader 1.4 has no public "rename" API except SettingsText.SetValue. The settings page is rebuilt from
// ModSetting.Name every time it opens, so updating Name is enough for the next open; the elements of a page that is
// open right now are relabeled as well. Both use internal MSCLoader members, read through reflection and guarded: if a
// future MSCLoader renames them, the language still applies after a game restart (the page is built in the stored
// language), it just cannot switch live.
//
// MSCLoader members used (1.4.2):
//   ModSetting.Name (internal field)            - text used when the page is built
//   ModSetting.SettingsElement (internal field) - the live element, destroyed when the page closes
//   ModSetting.HeaderElement (internal field)   - SettingsGroup of a header
//   SettingsElement.settingName (public Text field of an internal class)
//   SettingsGroup.HeaderTitle (public Text field of an internal class)
// Buttons and headers are upper-cased by MSCLoader when built; the same is done here.

using System;
using System.Collections.Generic;
using System.Reflection;
using MOPR6.Diagnostics;
using MSCLoader;
using UnityEngine.UI;

namespace MOPR6.Localization
{
    internal static class LocalizedSettings
    {
        enum Kind { Label, Text, Header, Button }

        sealed class Entry
        {
            public ModSetting Setting;
            public Func<string> Producer;
            public Kind Kind;
        }

        static readonly List<Entry> entries = new List<Entry>();

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo nameField = typeof(ModSetting).GetField("Name", Any);
        static readonly FieldInfo elementField = typeof(ModSetting).GetField("SettingsElement", Any);
        static readonly FieldInfo headerField = typeof(ModSetting).GetField("HeaderElement", Any);
        static bool warned;

        /// <summary>True if MSCLoader exposes what live relabeling needs.</summary>
        internal static bool CanRelabel => nameField != null;

        /// <summary>Forgets all registered settings. Called before the settings are built.</summary>
        internal static void Clear() => entries.Clear();

        internal static void Label(ModSetting setting, Func<string> producer) => Add(setting, producer, Kind.Label);
        internal static void Text(SettingsText setting, Func<string> producer) => Add(setting, producer, Kind.Text);
        internal static void Header(SettingsHeader setting, Func<string> producer) => Add(setting, producer, Kind.Header);
        internal static void Button(SettingsButton setting, Func<string> producer) => Add(setting, producer, Kind.Button);

        static void Add(ModSetting setting, Func<string> producer, Kind kind)
        {
            if (setting == null || producer == null) return;
            entries.Add(new Entry { Setting = setting, Producer = producer, Kind = kind });
        }

        /// <summary>Re-applies every registered text in the current language.</summary>
        internal static void Refresh()
        {
            if (!CanRelabel && !warned)
            {
                warned = true;
                ModConsole.Warning("MOPR6: " + Loc.T("con.language_restart"));
            }

            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                try
                {
                    string text = e.Producer() ?? string.Empty;
                    switch (e.Kind)
                    {
                        case Kind.Text:
                            // Public API: updates the name and the live label.
                            ((SettingsText)e.Setting).SetValue(text);
                            break;
                        case Kind.Label:
                            SetName(e.Setting, text);
                            SetElementText(e.Setting, "settingName", text);
                            break;
                        case Kind.Button:
                            SetName(e.Setting, text);
                            SetElementText(e.Setting, "settingName", text.ToUpper());
                            break;
                        case Kind.Header:
                            SetName(e.Setting, text);
                            SetHeaderTitle(e.Setting, text.ToUpper());
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Report("settings/Relabel", ex);
                }
            }

            RelabelModList();
        }

        /// <summary>
        /// The mod list entry of MOPR6 (MSCLoader's internal MenuElementList: public fields "mod" and "Description")
        /// shows Mod.Description as it was when the list was built; set it again in the new language.
        /// </summary>
        static void RelabelModList()
        {
            try
            {
                Mod self = MoprMod.Instance;
                if (self == null) return;
                foreach (UnityEngine.MonoBehaviour b in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.MonoBehaviour>())
                {
                    if (b == null || b.GetType().Name != "MenuElementList") continue;
                    if (!ReferenceEquals(b.GetType().GetField("mod", Any)?.GetValue(b), self)) continue;
                    Text description = b.GetType().GetField("Description", Any)?.GetValue(b) as Text;
                    if (description != null) description.text = self.Description;
                }
            }
            catch (Exception ex)
            {
                ModLog.Report("settings/ModList", ex);
            }
        }

        static void SetName(ModSetting setting, string text)
        {
            if (nameField != null) nameField.SetValue(setting, text);
        }

        /// <summary>Sets a Text field of the live SettingsElement (no-op when the page is closed).</summary>
        static void SetElementText(ModSetting setting, string field, string text)
        {
            if (elementField == null) return;
            UnityEngine.Object element = elementField.GetValue(setting) as UnityEngine.Object;
            if (element == null) return; // never built, or destroyed together with the closed page
            FieldInfo f = element.GetType().GetField(field, Any);
            Text label = f != null ? f.GetValue(element) as Text : null;
            if (label != null) label.text = text;
        }

        static void SetHeaderTitle(ModSetting setting, string text)
        {
            if (headerField == null) return;
            UnityEngine.Object group = headerField.GetValue(setting) as UnityEngine.Object;
            if (group == null) return;
            FieldInfo f = group.GetType().GetField("HeaderTitle", Any);
            Text title = f != null ? f.GetValue(group) as Text : null;
            if (title != null) title.text = text;
        }
    }
}
