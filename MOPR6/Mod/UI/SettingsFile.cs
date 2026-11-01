// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Reads an MSCLoader settings.json ({"isDisabled": false, "settings": [{"ID": "...", "Value": ...}]}) without
// Newtonsoft.Json.Linq: JObject needs System.ComponentModel.INotifyPropertyChanging, which the game's Mono 2.0 profile
// does not have (TypeLoadException when the calling method is compiled - it cannot even be caught there). A typed
// class with an object Value gets the plain value (bool, long, double, string), as MSCLoader's own SettingsList does.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MOPR6.UI
{
    internal static class SettingsFile
    {
        sealed class FileData
        {
            public List<Entry> settings = null;
        }

        sealed class Entry
        {
            public string ID = null;
            public object Value = null;
        }

        /// <summary>ID -> value of every setting in the file. Throws on a missing or broken file (callers report it).</summary>
        internal static Dictionary<string, object> Read(string path)
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            FileData data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
            if (data == null || data.settings == null) return values;
            foreach (Entry e in data.settings)
                if (e != null && e.ID != null) values[e.ID] = e.Value;
            return values;
        }

        internal static bool TryBool(Dictionary<string, object> values, string id, out bool value)
        {
            value = false;
            object o;
            if (!values.TryGetValue(id, out o) || !(o is bool)) return false;
            value = (bool)o;
            return true;
        }

        internal static bool TryInt(Dictionary<string, object> values, string id, out int value)
        {
            value = 0;
            object o;
            if (!values.TryGetValue(id, out o) || o == null) return false;
            if (o is long l && l >= int.MinValue && l <= int.MaxValue) { value = (int)l; return true; }
            if (o is int i) { value = i; return true; }
            return false;
        }
    }
}
