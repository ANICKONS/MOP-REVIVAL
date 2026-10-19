using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("MOP - REVIVAL")]
[assembly: AssemblyDescription("Modern Optimization Plugin - Revival 5 for My Summer Car. Based on MOP by Konrad Figura (Athlon).")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("ANICKON")]
[assembly: AssemblyProduct("MOP - REVIVAL")]
[assembly: AssemblyCopyright("Copyright (c) Konrad Figura 2019-2023, ANICKON 2026. GPLv3.")]
// MSCLoader asks mod authors to disclose AI generated code in this field.
[assembly: AssemblyTrademark("Contains AI-assisted code (Kiro). Original MOP code by Konrad Figura.")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("2b8f5d64-6c0e-4f4f-9d3a-5f0d2c6a8e71")]

[assembly: AssemblyVersion(MOP.BuildInfo.Version + ".0")]
[assembly: AssemblyFileVersion(MOP.BuildInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(MOP.BuildInfo.InformationalVersion)]

namespace MOP
{
    /// <summary>Single source of truth for the mod version.</summary>
    internal static class BuildInfo
    {
        public const string Version = "5.1.0";

#if STUB_REFERENCES
        /// <summary>True when this DLL was compiled against compile-only stubs instead of the real game assemblies.</summary>
        public static readonly bool BuiltWithStubs = true;
        public const string InformationalVersion = Version + "+stubs";
#else
        public static readonly bool BuiltWithStubs = false;
        public const string InformationalVersion = Version;
#endif
    }
}
