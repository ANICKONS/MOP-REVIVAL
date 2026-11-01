// Modern Optimization Plugin
// Copyright(C) 2019-2022 Athlon

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace MOP.Common
{
    // Stored in MOP config folder.
    class MopData
    {
        public string Version = "1.0";
#pragma warning disable CS0649 // kept for file compatibility with MOP 3.10 (rule server update time)
        public DateTime LastTimeUpdate;
#pragma warning restore CS0649
        public List<string> LastModList;
        // MOP - REVIVAL 5.0: MD5 of every .mopconfig MOPR 5.0 copied into the settings folder (file name -> hash).
        // 5.1 builds the rules into the DLL; RulesManager uses these hashes once to delete the copies nobody
        // edited, and empties the list.
#pragma warning disable CS0649 // written by the JSON reader
        public Dictionary<string, string> BundledRuleHashes;
#pragma warning restore CS0649
        // MOP - REVIVAL 5.1: interface language ("en" / "ru"), cached for building the settings page
        // before MSCLoader loads the saved dropdown value. Null = not chosen yet (use the system language).
        public string Language;
    }
}
