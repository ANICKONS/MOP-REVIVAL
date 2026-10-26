// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// NVIDIA driver profile access (NVAPI "DRS", the same database the NVIDIA app and NVIDIA Profile Inspector edit), for
// one executable: read, set and reset DWORD settings of the profile that owns it, creating that profile if the driver
// has none. No Unity or MOPR types, so the TestBot side tool (tools/NvProfileTool) uses the same file.
//
// Layouts and function IDs follow nvidiaProfileInspector (Orbmu2k, MIT; Native/NVAPI/NvapiDrsWrapper.cs), which
// works with current drivers; the structs are written by offset into native buffers (no marshalling of 4 KB inline
// strings, same behaviour on .NET and on the game's Mono). The setting IDs of Smooth Motion are not in the public
// NVAPI headers; they come from Profile Inspector's CustomSettingNames.xml.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MOP.Common
{
    public sealed class NvidiaProfile : IDisposable
    {
        // ----- Settings ---------------------------------------------------------------------------------

        /// <summary>Smooth Motion (driver frame generation): 0 off, 1 on. Driver 571.86+, RTX 40/50.</summary>
        public const uint SmoothMotionEnable = 0xB0D384C0;
        /// <summary>APIs Smooth Motion may run on: bit 1 DX12, 2 DX11, 4 Vulkan. Missing = all; current drivers ship 4.</summary>
        public const uint SmoothMotionApis = 0xB0CC0875;
        /// <summary>Smooth Motion log level 0..3, logs to C:\NvPresent\ (diagnostics only).</summary>
        public const uint SmoothMotionLogLevel = 0xB053C379;
        /// <summary>Smooth Motion draws coloured bars on generated frames (diagnostics only).</summary>
        public const uint SmoothMotionDebugBars = 0xB01B8B02;

        public const uint ApiDx12 = 1, ApiDx11 = 2, ApiVulkan = 4;

        // ----- NVAPI ------------------------------------------------------------------------------------

        const int StatusOk = 0;
        const int StatusSettingNotFound = -160;
        const int StatusProfileNotFound = -163;
        const int StatusExecutableNotFound = -166;

        const int StrBytes = 2048 * 2;                                  // NvAPI_UnicodeString
        const int UnionBytes = 4100;                                    // NVDRS_SETTING value union
        const int SettingSize = 4 + StrBytes + 5 * 4 + 2 * UnionBytes;  // NVDRS_SETTING_V1 = 12320
        const int ProfileSize = 4 + StrBytes + 4 * 4;                   // NVDRS_PROFILE_V1 = 4116
        const int AppSize = 4 + 4 + 4 * StrBytes + 4 + StrBytes;        // NVDRS_APPLICATION_V4 = 20492

        // NVDRS_SETTING_V1 offsets
        const int SetName = 4, SetId = SetName + StrBytes, SetType = SetId + 4, SetLocation = SetType + 4,
                  SetIsCurrentPredefined = SetLocation + 4, SetIsPredefinedValid = SetIsCurrentPredefined + 4,
                  SetPredefined = SetIsPredefinedValid + 4, SetCurrent = SetPredefined + UnionBytes;
        // NVDRS_PROFILE_V1 offsets
        const int ProfName = 4, ProfIsPredefined = ProfName + StrBytes + 4;
        // NVDRS_APPLICATION_V4 offsets
        const int AppIsPredefined = 4, AppName = 8;

        static uint Version(int size, int version) => (uint)(size | (version << 16));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterfaceFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionFn(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSessionFn(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FindAppFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName, out IntPtr profile, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int FindProfileFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateProfileFn(IntPtr session, IntPtr info, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileFn(IntPtr session, IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileDataFn(IntPtr session, IntPtr profile, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting, IntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr setting, uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ErrorMessageFn(int status, StringBuilder text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DriverVersionFn(out uint version, StringBuilder branch);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        static QueryInterfaceFn query;
        static VoidFn initialize;
        static CreateSessionFn createSession;
        static SessionFn destroySession, loadSettings, saveSettings;
        static FindAppFn findApplication;
        static FindProfileFn findProfile;
        static CreateProfileFn createProfile;
        static ProfileFn deleteProfile;
        static ProfileDataFn createApplication, getProfileInfo;
        static GetSettingFn getSetting;
        static SetSettingFn setSetting;
        static DeleteSettingFn deleteSetting;
        static ErrorMessageFn errorMessage;
        static DriverVersionFn driverVersion;
        static string loadError;
        static bool loaded;

        static T Fn<T>(uint id, uint fallback = 0) where T : class
        {
            IntPtr p = query(id);
            if (p == IntPtr.Zero && fallback != 0) p = query(fallback);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>Loads nvapi64.dll (once). Null on success, else why not (no NVIDIA driver, 32-bit...).</summary>
        public static string Load()
        {
            if (loaded) return loadError;
            loaded = true;
            try
            {
                IntPtr lib = LoadLibraryW(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
                if (lib == IntPtr.Zero) return loadError = "nvapi not found (no NVIDIA driver)";
                IntPtr q = GetProcAddress(lib, "nvapi_QueryInterface");
                if (q == IntPtr.Zero) return loadError = "nvapi_QueryInterface not found";
                query = (QueryInterfaceFn)Marshal.GetDelegateForFunctionPointer(q, typeof(QueryInterfaceFn));

                initialize = Fn<VoidFn>(0x0150E828);
                errorMessage = Fn<ErrorMessageFn>(0x6C2D048C);
                driverVersion = Fn<DriverVersionFn>(0x2926AAAD);
                createSession = Fn<CreateSessionFn>(0x0694D52E);
                destroySession = Fn<SessionFn>(0xDAD9CFF8);
                loadSettings = Fn<SessionFn>(0x375DBD6B);
                saveSettings = Fn<SessionFn>(0xFCBC7E14);
                findApplication = Fn<FindAppFn>(0xEEE566B2);
                findProfile = Fn<FindProfileFn>(0x7E4A9A0B);
                createProfile = Fn<CreateProfileFn>(0xCC176068);
                deleteProfile = Fn<ProfileFn>(0x17093206);
                createApplication = Fn<ProfileDataFn>(0x4347A9DE);
                getProfileInfo = Fn<ProfileDataFn>(0x61CD6FD6);
                // As Profile Inspector: the newer entry points first (they also take settings the public ones
                // refuse), the public NVAPI ones as fallback (cdecl: the extra arguments are ignored).
                getSetting = Fn<GetSettingFn>(0xEA99498D, 0x73BF8338);
                setSetting = Fn<SetSettingFn>(0x8A2CF5F5, 0x577DD202);
                deleteSetting = Fn<DeleteSettingFn>(0xD20D29DF, 0xE4A26362);

                if (initialize == null || createSession == null || destroySession == null || loadSettings == null || saveSettings == null ||
                    findApplication == null || findProfile == null || createProfile == null || createApplication == null ||
                    getProfileInfo == null || getSetting == null || setSetting == null || deleteSetting == null)
                    return loadError = "nvapi DRS functions not found";
                int s = initialize();
                if (s != StatusOk) return loadError = "NvAPI_Initialize: " + Describe(s);
                return loadError = null;
            }
            catch (Exception ex)
            {
                return loadError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static string Describe(int status)
        {
            if (errorMessage != null)
            {
                StringBuilder sb = new StringBuilder(64);
                try { if (errorMessage(status, sb) == StatusOk && sb.Length > 0) return sb + " (" + status + ")"; } catch { }
            }
            return "status " + status;
        }

        /// <summary>Driver version as "617.14" (or null).</summary>
        public static string DriverVersion()
        {
            if (Load() != null || driverVersion == null) return null;
            StringBuilder branch = new StringBuilder(64);
            if (driverVersion(out uint v, branch) != StatusOk) return null;
            return (v / 100) + "." + (v % 100).ToString("00");
        }

        // ----- Session and profile ----------------------------------------------------------------------

        IntPtr session, profile;
        public string Executable { get; private set; }
        public string ProfileName { get; private set; }
        public bool ProfilePredefined { get; private set; }
        public bool ProfileFound => profile != IntPtr.Zero;

        NvidiaProfile() { }

        /// <summary>
        /// Opens the driver settings and finds the profile of <paramref name="exe"/> (file name, e.g. "mysummercar.exe").
        /// Null and an error when NVAPI is not usable. The profile may be missing (<see cref="ProfileFound"/>).
        /// </summary>
        public static NvidiaProfile Open(string exe, out string error)
        {
            error = Load();
            if (error != null) return null;
            NvidiaProfile p = new NvidiaProfile { Executable = exe };
            int s = createSession(out p.session);
            if (s != StatusOk) { error = "DRS_CreateSession: " + Describe(s); return null; }
            s = loadSettings(p.session);
            if (s != StatusOk) { error = "DRS_LoadSettings: " + Describe(s); p.Dispose(); return null; }
            error = p.FindApplication();
            if (error != null) { p.Dispose(); return null; }
            return p;
        }

        string FindApplication()
        {
            IntPtr app = Alloc(AppSize);
            try
            {
                Marshal.WriteInt32(app, 0, (int)Version(AppSize, 4));
                int s = findApplication(session, Executable, out IntPtr found, app);
                if (s == StatusExecutableNotFound || s == StatusProfileNotFound) { profile = IntPtr.Zero; return null; }
                if (s != StatusOk) return "DRS_FindApplicationByName: " + Describe(s);
                profile = found;
                return ReadProfileInfo();
            }
            finally { Marshal.FreeHGlobal(app); }
        }

        string ReadProfileInfo()
        {
            IntPtr info = Alloc(ProfileSize);
            try
            {
                Marshal.WriteInt32(info, 0, (int)Version(ProfileSize, 1));
                int s = getProfileInfo(session, profile, info);
                if (s != StatusOk) return "DRS_GetProfileInfo: " + Describe(s);
                ProfileName = ReadString(info, ProfName);
                ProfilePredefined = Marshal.ReadInt32(info, ProfIsPredefined) != 0;
                return null;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        /// <summary>Creates a user profile <paramref name="name"/> holding the executable (when it has none yet).</summary>
        public string CreateProfile(string name)
        {
            if (ProfileFound) return null;
            int s = findProfile(session, name, out IntPtr existing);
            if (s == StatusOk) return "a profile named \"" + name + "\" exists without " + Executable;
            IntPtr info = Alloc(ProfileSize);
            IntPtr app = Alloc(AppSize);
            try
            {
                Marshal.WriteInt32(info, 0, (int)Version(ProfileSize, 1));
                WriteString(info, ProfName, name);
                s = createProfile(session, info, out IntPtr created);
                if (s != StatusOk) return "DRS_CreateProfile: " + Describe(s);
                Marshal.WriteInt32(app, 0, (int)Version(AppSize, 4));
                WriteString(app, AppName, Executable);
                s = createApplication(session, created, app);
                if (s != StatusOk)
                {
                    try { deleteProfile?.Invoke(session, created); } catch { }
                    return "DRS_CreateApplication: " + Describe(s);
                }
                profile = created;
                return ReadProfileInfo();
            }
            finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(app); }
        }

        /// <summary>Deletes the executable's profile (only for one this code created, see the caller).</summary>
        public string DeleteProfile()
        {
            if (!ProfileFound) return null;
            if (deleteProfile == null) return "DRS_DeleteProfile not found";
            int s = deleteProfile(session, profile);
            if (s != StatusOk) return "DRS_DeleteProfile: " + Describe(s);
            profile = IntPtr.Zero;
            ProfileName = null;
            return null;
        }

        // ----- Settings ---------------------------------------------------------------------------------

        public struct Value
        {
            /// <summary>The profile itself holds the setting (user or NVIDIA predefined); else inherited or missing.</summary>
            public bool InProfile;
            /// <summary>The value is NVIDIA's predefined one for this profile (not changed by the user).</summary>
            public bool Predefined;
            /// <summary>Found at all (in the profile, the global or the base profile).</summary>
            public bool Found;
            public uint Current;
            public int Location;   // 0 current profile, 1 global, 2 base, 3 default

            public override string ToString() =>
                !Found ? "-" : "0x" + Current.ToString("X8") + (InProfile ? Predefined ? " (NVIDIA default)" : " (set)" : " (inherited)");
        }

        public string Get(uint id, out Value value)
        {
            value = new Value();
            if (!ProfileFound) return "no profile";
            IntPtr set = Alloc(SettingSize);
            IntPtr extra = Alloc(16);
            try
            {
                Marshal.WriteInt32(set, 0, (int)Version(SettingSize, 1));
                int s = getSetting(session, profile, id, set, extra);
                if (s == StatusSettingNotFound) return null;
                if (s != StatusOk) return "DRS_GetSetting: " + Describe(s);
                value.Found = true;
                value.Location = Marshal.ReadInt32(set, SetLocation);
                value.InProfile = value.Location == 0;
                value.Predefined = Marshal.ReadInt32(set, SetIsCurrentPredefined) != 0;
                value.Current = (uint)Marshal.ReadInt32(set, SetCurrent);
                return null;
            }
            finally { Marshal.FreeHGlobal(set); Marshal.FreeHGlobal(extra); }
        }

        public string Set(uint id, uint dword)
        {
            if (!ProfileFound) return "no profile";
            IntPtr set = Alloc(SettingSize);
            try
            {
                Marshal.WriteInt32(set, 0, (int)Version(SettingSize, 1));
                Marshal.WriteInt32(set, SetId, (int)id);
                Marshal.WriteInt32(set, SetType, 0);       // NVDRS_DWORD_TYPE
                Marshal.WriteInt32(set, SetLocation, 0);   // NVDRS_CURRENT_PROFILE_LOCATION
                Marshal.WriteInt32(set, SetCurrent, (int)dword);
                int s = setSetting(session, profile, set, 0, 0);
                return s == StatusOk ? null : "DRS_SetSetting 0x" + id.ToString("X8") + ": " + Describe(s);
            }
            finally { Marshal.FreeHGlobal(set); }
        }

        /// <summary>Removes the user's value: back to NVIDIA's predefined value, or inherited when there is none.</summary>
        public string Reset(uint id)
        {
            if (!ProfileFound) return "no profile";
            int s = deleteSetting(session, profile, id);
            return s == StatusOk || s == StatusSettingNotFound ? null : "DRS_DeleteProfileSetting 0x" + id.ToString("X8") + ": " + Describe(s);
        }

        /// <summary>Writes the changes to the driver (they apply to processes started afterwards).</summary>
        public string Save()
        {
            int s = saveSettings(session);
            return s == StatusOk ? null : "DRS_SaveSettings: " + Describe(s);
        }

        public void Dispose()
        {
            if (session != IntPtr.Zero)
            {
                try { destroySession(session); } catch { }
                session = IntPtr.Zero;
            }
            profile = IntPtr.Zero;
        }

        // ----- Buffers ----------------------------------------------------------------------------------

        static IntPtr Alloc(int size)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            return p;
        }

        static void WriteString(IntPtr buffer, int offset, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text ?? "");
            int n = Math.Min(bytes.Length, StrBytes - 2);
            Marshal.Copy(bytes, 0, new IntPtr(buffer.ToInt64() + offset), n);
        }

        static string ReadString(IntPtr buffer, int offset)
        {
            byte[] bytes = new byte[StrBytes];
            Marshal.Copy(new IntPtr(buffer.ToInt64() + offset), bytes, 0, StrBytes);
            string s = Encoding.Unicode.GetString(bytes);
            int end = s.IndexOf('\0');
            return end >= 0 ? s.Substring(0, end) : s;
        }
    }
}
