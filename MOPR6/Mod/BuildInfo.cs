// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Reflection;

[assembly: AssemblyTitle("MOPR6")]
[assembly: AssemblyProduct("MOPR6")]
[assembly: AssemblyCopyright("Copyright (C) 2026 ANICKON, GPLv3")]
[assembly: AssemblyTrademark("Contains AI-assisted code (Kiro).")]
[assembly: AssemblyVersion("6.0.0.1")]
[assembly: AssemblyFileVersion("6.0.0.1")]
[assembly: AssemblyInformationalVersion(MOPR6.BuildInfo.Version)]

namespace MOPR6
{
    static class BuildInfo
    {
        /// <summary>
        /// The one place of the version. Stays "-preview.N" until the release gate (requirement 4.4) is met;
        /// the assembly version above is 6.0.0.N for the same preview.
        /// </summary>
        public const string Version = "6.0.0-preview.1";
    }
}
