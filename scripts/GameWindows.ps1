<#
.SYNOPSIS
    Win32 helpers for the test runner: find the windows of the game process, inspect dialogs, press buttons,
    click inside the game window and take screenshots. Dot-source it: . "$PSScriptRoot\GameWindows.ps1"
#>

if (-not ('MoprWin32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class MoprWin32
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref Point p);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Left click at a point in client coordinates, but ONLY if the window that would receive the click (the top
    /// window under that screen point) is the given window. Returns false (no click) otherwise, so a click can
    /// never land in another application that covers the game.
    /// </summary>
    public static bool ClickClientIfOnTop(IntPtr h, int x, int y)
    {
        if (IsIconic(h)) return false;
        Point p = new Point(x, y);
        if (!ClientToScreen(h, ref p)) return false;
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(150);
        IntPtr under = WindowFromPoint(p);
        if (under == IntPtr.Zero || GetAncestor(under, 2 /* GA_ROOT */) != h) return false;
        SetCursorPos(p.X, p.Y);
        System.Threading.Thread.Sleep(80);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    public const uint BM_CLICK = 0x00F5, WM_COMMAND = 0x0111;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

    public static List<IntPtr> TopWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == (uint)pid) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static List<IntPtr> Children(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Left click at a point given in client coordinates of the window.</summary>
    public static void ClickClient(IntPtr h, int x, int y)
    {
        Point p = new Point(x, y);
        ClientToScreen(h, ref p);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(150);
        SetCursorPos(p.X, p.Y);
        System.Threading.Thread.Sleep(80);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Screenshot of the whole virtual screen, scaled to the given width, saved as PNG.</summary>
    public static void Screenshot(string path, int width)
    {
        Rectangle b = System.Windows.Forms.SystemInformation.VirtualScreen;
        using (Bitmap full = new Bitmap(b.Width, b.Height))
        {
            using (Graphics g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            int h = (int)((long)b.Height * width / b.Width);
            using (Bitmap small = new Bitmap(full, new Size(width, h))) small.Save(path, ImageFormat.Png);
        }
    }
}
'@ -ReferencedAssemblies System.Drawing, System.Windows.Forms
}

function Get-GameWindows([int]$ProcessId) {
    foreach ($h in [MoprWin32]::TopWindows($ProcessId)) {
        $r = New-Object MoprWin32+RECT
        [void][MoprWin32]::GetWindowRect($h, [ref]$r)
        [pscustomobject]@{
            Handle  = $h
            Title   = [MoprWin32]::Text($h)
            Class   = [MoprWin32]::Class($h)
            Visible = [MoprWin32]::IsWindowVisible($h)
            Width   = $r.Right - $r.Left
            Height  = $r.Bottom - $r.Top
        }
    }
}

function Get-DialogControls([IntPtr]$Dialog) {
    foreach ($c in [MoprWin32]::Children($Dialog)) {
        [pscustomobject]@{ Handle = $c; Class = [MoprWin32]::Class($c); Text = [MoprWin32]::Text($c); Id = [MoprWin32]::GetDlgCtrlID($c); Visible = [MoprWin32]::IsWindowVisible($c) }
    }
}

<#
    Presses the confirm button of a standard Win32 dialog (Unity's "Configuration" window and similar):
    the control with ID 1 (IDOK), else a button whose text looks like OK / Play. Returns the pressed control or $null.
#>
function Invoke-DialogConfirm([IntPtr]$Dialog) {
    $controls = @(Get-DialogControls $Dialog | Where-Object { $_.Class -eq 'Button' -and $_.Visible })
    $target = $controls | Where-Object { $_.Id -eq 1 } | Select-Object -First 1
    # ASCII only in this file (Windows PowerShell reads BOM-less files as ANSI): \u041E\u041A = Russian "OK".
    if (-not $target) { $target = $controls | Where-Object { $_.Text -match '^\s*&?(OK|Ok|\u041E\u041A|Play!?)\s*$' } | Select-Object -First 1 }
    if (-not $target) { return $null }
    [void][MoprWin32]::PostMessage($target.Handle, [MoprWin32]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
    return $target
}

function Test-LogContains([string]$Path, [string]$Text, [datetime]$NotBefore) {
    # The game rewrites its log on every launch: ignore a file older than the launch (previous session).
    try {
        $item = Get-Item $Path -ErrorAction Stop
        if ($item.LastWriteTime -lt $NotBefore) { return $false }
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try { $content = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Close() }
        return $content.Contains($Text)
    } catch { return $false }
}

function Save-Screenshot([string]$Path, [int]$Width = 1280) {
    try { [MoprWin32]::Screenshot($Path, $Width); return $true } catch { return $false }
}
