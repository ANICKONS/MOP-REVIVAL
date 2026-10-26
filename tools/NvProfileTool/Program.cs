// NvProfileTool - see NvProfileTool.csproj.
//   status                      driver, profile of the exe, Smooth Motion settings
//   set <id> <value>            set a DWORD (hex ids/values like 0xB0D384C0 or decimal)
//   reset <id>                  remove the user's value (back to NVIDIA's or inherited)
//   create <profile name>       create a user profile holding the exe (when it has none)
//   delete-profile              delete the exe's profile (only one you created!)
// Options: --exe <file name> (default mysummercar.exe). Changes apply to processes started afterwards.

using System;
using System.Globalization;
using MOP.Common;

static class Program
{
    static int Main(string[] args)
    {
        string exe = "mysummercar.exe";
        var rest = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--exe" && i + 1 < args.Length) exe = args[++i];
            else rest.Add(args[i]);
        }
        string cmd = rest.Count > 0 ? rest[0].ToLowerInvariant() : "status";

        string err = NvidiaProfile.Load();
        if (err != null) { Console.WriteLine("NVAPI: " + err); return 2; }
        Console.WriteLine("Driver " + (NvidiaProfile.DriverVersion() ?? "?"));

        using (NvidiaProfile p = NvidiaProfile.Open(exe, out err))
        {
            if (p == null) { Console.WriteLine("Open: " + err); return 2; }
            switch (cmd)
            {
                case "status":
                    break;
                case "set":
                    err = p.Set(Parse(rest[1]), Parse(rest[2])) ?? p.Save();
                    break;
                case "reset":
                    err = p.Reset(Parse(rest[1])) ?? p.Save();
                    break;
                case "create":
                    err = p.CreateProfile(rest[1]) ?? p.Save();
                    break;
                case "delete-profile":
                    err = p.DeleteProfile() ?? p.Save();
                    break;
                default:
                    Console.WriteLine("unknown command " + cmd);
                    return 1;
            }
            if (err != null) { Console.WriteLine(cmd + ": " + err); return 3; }
            Print(p);
        }
        return 0;
    }

    static void Print(NvidiaProfile p)
    {
        Console.WriteLine("Exe " + p.Executable + ": " + (p.ProfileFound ? "profile \"" + p.ProfileName + "\"" + (p.ProfilePredefined ? " (NVIDIA)" : " (user)") : "no profile"));
        if (!p.ProfileFound) return;
        Show(p, "Smooth Motion - Enable      ", NvidiaProfile.SmoothMotionEnable);
        Show(p, "Smooth Motion - Enabled APIs", NvidiaProfile.SmoothMotionApis);
        Show(p, "Smooth Motion - Log level   ", NvidiaProfile.SmoothMotionLogLevel);
        Show(p, "Smooth Motion - Debug bars  ", NvidiaProfile.SmoothMotionDebugBars);
    }

    static void Show(NvidiaProfile p, string name, uint id)
    {
        string err = p.Get(id, out NvidiaProfile.Value v);
        Console.WriteLine("  " + name + " 0x" + id.ToString("X8") + ": " + (err ?? v + (v.Found ? " location " + v.Location : "")));
    }

    static uint Parse(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(s, CultureInfo.InvariantCulture);
}
