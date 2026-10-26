// FrameCounter - see FrameCounter.csproj. Raw COM through vtable slots (no wrapper packages).

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static unsafe class Program
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels,
        uint levels, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

    static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
    const int WaitTimeout = unchecked((int)0x887A0027), AccessLost = unchecked((int)0x887A0026);

    static IntPtr* V(IntPtr com) => *(IntPtr**)com;
    static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(com)[2])(com);
    static int QueryInterface(IntPtr com, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(com)[0])(com, &iid, &r);
        result = r;
        return hr;
    }

    static int Main(string[] args)
    {
        string csv = args.Length > 0 ? args[0] : "frames.csv";
        string processName = "mysummercar";
        double seconds = 3600;
        if (args.Length > 1) processName = args[1];
        if (args.Length > 2) seconds = double.Parse(args[2], CultureInfo.InvariantCulture);

        IntPtr device = IntPtr.Zero, dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero, dup = IntPtr.Zero;
        using StreamWriter w = new StreamWriter(csv, false, new UTF8Encoding(false)) { AutoFlush = true };
        w.WriteLine("time,displayed,gameframes,accumulated,game,nvpresent,output,clock");
        try
        {
            int level = 0xb000, got;
            IntPtr ctx;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0, &level, 1, 7, &device, &got, &ctx);
            if (hr < 0) { Console.WriteLine("D3D11CreateDevice 0x" + hr.ToString("X8")); return 2; }
            Release(ctx);
            hr = QueryInterface(device, IID_IDXGIDevice, out dxgiDevice);
            if (hr < 0) { Console.WriteLine("IDXGIDevice 0x" + hr.ToString("X8")); return 2; }
            IntPtr a;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dxgiDevice)[7])(dxgiDevice, &a);   // IDXGIDevice::GetAdapter
            if (hr < 0) { Console.WriteLine("GetAdapter 0x" + hr.ToString("X8")); return 2; }
            adapter = a;

            Stopwatch clock = Stopwatch.StartNew();
            int outputIndex = -1;
            long lastBucket = -1;
            int displayed = 0, accumulated = 0, gameFrames = 0;
            bool game = false, nvpresent = false;
            double nextProbe = 0;
            RECT window = default, origin = default;   // window in output coordinates
            byte* info = stackalloc byte[48];   // DXGI_OUTDUPL_FRAME_INFO
            const int MaxRects = 512;
            RECT* rects = stackalloc RECT[MaxRects];

            while (clock.Elapsed.TotalSeconds < seconds)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now >= nextProbe)
                {
                    nextProbe = now + 2;
                    Probe(processName, out game, out nvpresent, out RECT rect);
                    int wanted = game ? OutputOf(adapter, rect, out origin) : 0;
                    if (wanted < 0) wanted = 0;
                    // A little inside the window: borders and the title bar change with focus, not with frames.
                    window = new RECT { Left = rect.Left - origin.Left + 16, Top = rect.Top - origin.Top + 48,
                                        Right = rect.Right - origin.Left - 16, Bottom = rect.Bottom - origin.Top - 16 };
                    if (dup == IntPtr.Zero || wanted != outputIndex)
                    {
                        Release(dup);
                        dup = Duplicate(adapter, device, wanted, out string err);
                        outputIndex = wanted;
                        if (dup == IntPtr.Zero) { Console.WriteLine("DuplicateOutput: " + err); Thread.Sleep(1000); continue; }
                    }
                }
                if (dup == IntPtr.Zero) { Thread.Sleep(100); continue; }

                IntPtr resource;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)V(dup)[8])(dup, 50, info, &resource);   // AcquireNextFrame
                long bucket = (long)Math.Floor(clock.Elapsed.TotalSeconds);
                if (bucket != lastBucket)
                {
                    if (lastBucket >= 0)
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7:yyyy-MM-ddTHH:mm:ss}", lastBucket, displayed, gameFrames, accumulated, game ? 1 : 0, nvpresent ? 1 : 0, outputIndex, DateTime.Now));
                    lastBucket = bucket;
                    displayed = accumulated = gameFrames = 0;
                }
                if (hr == WaitTimeout) continue;
                if (hr == AccessLost) { Release(dup); dup = IntPtr.Zero; nextProbe = 0; continue; }
                if (hr < 0) { Console.WriteLine("AcquireNextFrame 0x" + hr.ToString("X8")); Release(dup); dup = IntPtr.Zero; Thread.Sleep(200); nextProbe = 0; continue; }
                long lastPresent = *(long*)info;
                uint acc = *(uint*)(info + 16);
                uint metadata = *(uint*)(info + 40);
                if (lastPresent != 0)
                {
                    displayed++;
                    // Only the images in which the game window changed (the rest of the desktop animates too).
                    if (game && metadata > 0)
                    {
                        uint required;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, RECT*, uint*, int>)V(dup)[9])(dup, (uint)(MaxRects * sizeof(RECT)), rects, &required);   // GetFrameDirtyRects
                        if (hr >= 0)
                        {
                            int n = (int)(required / (uint)sizeof(RECT));
                            for (int k = 0; k < n && k < MaxRects; k++)
                                if (rects[k].Left < window.Right && rects[k].Right > window.Left && rects[k].Top < window.Bottom && rects[k].Bottom > window.Top)
                                { gameFrames++; break; }
                        }
                        else gameFrames++;   // too many rects to list: count it
                    }
                }
                accumulated += (int)acc;
                Release(resource);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(dup)[14])(dup);   // ReleaseFrame

                if (!game && clock.Elapsed.TotalSeconds > 30 && Process.GetProcessesByName(processName).Length == 0 && lastBucket > 60) break;
            }
        }
        finally
        {
            Release(dup); Release(adapter); Release(dxgiDevice); Release(device);
        }
        return 0;
    }

    static void Probe(string name, out bool running, out bool nvpresent, out RECT rect)
    {
        running = nvpresent = false;
        rect = default;
        Process p = Process.GetProcessesByName(name).FirstOrDefault();
        if (p == null) return;
        running = true;
        try { nvpresent = p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "NvPresent64.dll", StringComparison.OrdinalIgnoreCase)); } catch { }
        try { if (p.MainWindowHandle != IntPtr.Zero && !IsIconic(p.MainWindowHandle)) GetWindowRect(p.MainWindowHandle, out rect); } catch { }
    }

    /// <summary>Index of the adapter output whose desktop rectangle holds the window centre, or -1.</summary>
    static int OutputOf(IntPtr adapter, RECT window, out RECT bounds)
    {
        bounds = default;
        int cx = (window.Left + window.Right) / 2, cy = (window.Top + window.Bottom) / 2;
        byte* desc = stackalloc byte[96];   // DXGI_OUTPUT_DESC: DeviceName[32] WCHAR, RECT, BOOL, rotation, HMONITOR
        for (uint i = 0; i < 8; i++)
        {
            IntPtr output;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(adapter)[7])(adapter, i, &output);   // EnumOutputs
            if (hr < 0) break;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(output)[7])(output, desc);   // IDXGIOutput::GetDesc
            Release(output);
            if (hr < 0) continue;
            int* r = (int*)(desc + 64);
            if (cx >= r[0] && cx < r[2] && cy >= r[1] && cy < r[3])
            {
                bounds = new RECT { Left = r[0], Top = r[1], Right = r[2], Bottom = r[3] };
                return (int)i;
            }
        }
        return -1;
    }

    static IntPtr Duplicate(IntPtr adapter, IntPtr device, int index, out string error)
    {
        error = null;
        IntPtr output, output1, dup;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(adapter)[7])(adapter, (uint)index, &output);
        if (hr < 0) { error = "EnumOutputs 0x" + hr.ToString("X8"); return IntPtr.Zero; }
        hr = QueryInterface(output, IID_IDXGIOutput1, out output1);
        Release(output);
        if (hr < 0) { error = "IDXGIOutput1 0x" + hr.ToString("X8"); return IntPtr.Zero; }
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)V(output1)[22])(output1, device, &dup);   // DuplicateOutput
        Release(output1);
        if (hr < 0) { error = "0x" + hr.ToString("X8"); return IntPtr.Zero; }
        return dup;
    }
}
