<#
.SYNOPSIS
    Compares two screenshots pixel by pixel: share of differing pixels, mean and max channel delta,
    and the bounding box of the differences. Optionally writes a difference image.
#>
param(
    [Parameter(Mandatory)][string]$A,
    [Parameter(Mandatory)][string]$B,
    [int]$Threshold = 8,
    [string]$DiffOut = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ia = [System.Drawing.Bitmap]::FromFile((Resolve-Path $A))
$ib = [System.Drawing.Bitmap]::FromFile((Resolve-Path $B))
try {
    if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) { throw "Different sizes: $($ia.Width)x$($ia.Height) vs $($ib.Width)x$($ib.Height)" }
    $w = $ia.Width; $h = $ia.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    $da = $ia.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
    $db = $ib.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
    $n = $da.Stride * $h
    $ba = New-Object byte[] $n; $bb = New-Object byte[] $n
    [System.Runtime.InteropServices.Marshal]::Copy($da.Scan0, $ba, 0, $n)
    [System.Runtime.InteropServices.Marshal]::Copy($db.Scan0, $bb, 0, $n)
    $ia.UnlockBits($da); $ib.UnlockBits($db)

    $diff = 0; $sum = 0.0; $max = 0; $x0 = $w; $y0 = $h; $x1 = -1; $y1 = -1
    $out = if ($DiffOut) { New-Object byte[] $n } else { $null }
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $da.Stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $row + $x * 4
            $d = [Math]::Max([Math]::Abs($ba[$i] - $bb[$i]), [Math]::Max([Math]::Abs($ba[$i+1] - $bb[$i+1]), [Math]::Abs($ba[$i+2] - $bb[$i+2])))
            $sum += $d
            if ($d -gt $max) { $max = $d }
            if ($d -gt $Threshold) {
                $diff++
                if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }
                if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
            }
            if ($out) { $v = [byte][Math]::Min(255, $d * 4); $out[$i] = $v; $out[$i+1] = $v; $out[$i+2] = $v; $out[$i+3] = 255 }
        }
    }
    $total = $w * $h
    $box = if ($x1 -ge 0) { "($x0,$y0)-($x1,$y1)" } else { 'none' }
    "{0}: {1:0.000}% pixels differ by more than {2}, mean delta {3:0.000}, max delta {4}, box {5}" -f (Split-Path $B -Leaf), (100.0 * $diff / $total), $Threshold, ($sum / $total), $max, $box

    if ($out) {
        $bmp = New-Object System.Drawing.Bitmap $w, $h, $fmt
        $dd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $fmt)
        [System.Runtime.InteropServices.Marshal]::Copy($out, 0, $dd.Scan0, $n)
        $bmp.UnlockBits($dd)
        $bmp.Save($DiffOut, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
}
finally {
    $ia.Dispose(); $ib.Dispose()
}
