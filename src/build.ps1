# Builds ..\dist\VpnPanel.exe and the installer ..\dist\VPN Panel Setup.exe from the .cs/.xaml files here,
# with the C# compiler that ships with Windows (.NET Framework 4.8).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File C:\VPN\src\build.ps1 [-Target All|App|Setup]
#   All   (default) builds both.
#   App   builds only dist\VpnPanel.exe.
#   Setup builds only the installer, embedding whatever dist\VpnPanel.exe is there. The release workflow
#         uses App -> sign -> Setup -> sign, so the installer carries the already-signed app.

param([ValidateSet('All', 'App', 'Setup')][string]$Target = 'All')

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path (Split-Path $src) 'dist'
$out = Join-Path $dist 'VpnPanel.exe'
$setup = Join-Path $dist 'VPN Panel Setup.exe'
New-Item -ItemType Directory -Force $dist | Out-Null
$ico = Join-Path $src 'vpn.ico'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Add-Type -AssemblyName System.Drawing

# App icon: stained-glass raven & crescent moon medallion
function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $cx = $s / 2.0
    $g.TranslateTransform($cx, $cx)
    $scale = $cx / 64.0
    $g.ScaleTransform($scale, $scale)

    $iconMode = ($s -le 64)
    $leadWidth = if ($iconMode) { 2.2 } else { 1.8 }
    $leadPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(10, 10, 14), $leadWidth)
    $leadPen.StartCap = 'Round'; $leadPen.EndCap = 'Round'; $leadPen.LineJoin = 'Round'

    $gold1 = [Drawing.Color]::FromArgb(235, 172, 34)
    $gold2 = [Drawing.Color]::FromArgb(247, 212, 104)
    $ringBrush = New-Object Drawing.Drawing2D.LinearGradientBrush (
        (New-Object Drawing.PointF -62, -62),
        (New-Object Drawing.PointF 62, 62),
        $gold2, $gold1
    )
    $g.FillEllipse($ringBrush, -62, -62, 124, 124)
    $g.DrawEllipse($leadPen, -62, -62, 124, 124)

    if (-not $iconMode) {
        foreach ($ang in @(25, 70, 115, 155, 205, 250, 295, 335)) {
            $rad = $ang * [Math]::PI / 180.0
            $x1 = [float]([Math]::Cos($rad) * 51.0)
            $y1 = [float]([Math]::Sin($rad) * 51.0)
            $x2 = [float]([Math]::Cos($rad) * 62.0)
            $y2 = [float]([Math]::Sin($rad) * 62.0)
            $g.DrawLine($leadPen, $x1, $y1, $x2, $y2)
        }
    }

    $rubyLight = [Drawing.Color]::FromArgb(208, 32, 36)
    $rubyDark  = [Drawing.Color]::FromArgb(115, 12, 18)
    $coreBrush = New-Object Drawing.Drawing2D.LinearGradientBrush (
        (New-Object Drawing.PointF -40, -51),
        (New-Object Drawing.PointF 40, 51),
        $rubyLight, $rubyDark
    )
    $g.FillEllipse($coreBrush, -51, -51, 102, 102)

    if (-not $iconMode) {
        $facetBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 80, 5, 10))
        $g.FillPie($facetBrush, -51, -51, 102, 102, -30, 50)
        $g.FillPie($facetBrush, -51, -51, 102, 102, 110, 55)
        $g.FillPie($facetBrush, -51, -51, 102, 102, 215, 45)
        $facetBrush.Dispose()
        $g.DrawLine($leadPen, -49, -12, 49, 15)
        $g.DrawLine($leadPen, -36, 36, 38, -34)
    }
    $g.DrawEllipse($leadPen, -51, -51, 102, 102)

    $moonBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(250, 238, 145))
    $moonPath = New-Object Drawing.Drawing2D.GraphicsPath
    $moonPath.AddBezier(4, -38, -38, -34, -44, 22, 6, 26)
    $moonPath.AddBezier(6, 26, 22, 26, 34, 16, 40, 4)
    $moonPath.AddBezier(40, 4, 26, 14, 4, 14, -12, 2)
    $moonPath.AddBezier(-12, 2, -26, -10, -18, -30, 4, -38)
    $moonPath.CloseFigure()
    $g.FillPath($moonBrush, $moonPath)
    $g.DrawPath($leadPen, $moonPath)

    $starBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 234, 90))
    $starPts = New-Object System.Collections.Generic.List[Drawing.PointF]
    $sx = 29.0; $sy = -11.0
    for ($i = 0; $i -lt 16; $i++) {
        $a = ($i * 22.5 - 90) * [Math]::PI / 180.0
        $r = if ($i % 2 -eq 0) { if ($i % 4 -eq 0) { 11.5 } else { 8.0 } } else { 3.4 }
        $starPts.Add((New-Object Drawing.PointF ($sx + [float]([Math]::Cos($a) * $r)), ($sy + [float]([Math]::Sin($a) * $r))))
    }
    $g.FillPolygon($starBrush, $starPts.ToArray())
    $g.DrawPolygon($leadPen, $starPts.ToArray())

    if (-not $iconMode) {
        $dPts = [Drawing.PointF[]]@(
            (New-Object Drawing.PointF -29, 11),
            (New-Object Drawing.PointF -26, 17),
            (New-Object Drawing.PointF -21, 19),
            (New-Object Drawing.PointF -26, 21),
            (New-Object Drawing.PointF -29, 27),
            (New-Object Drawing.PointF -32, 21),
            (New-Object Drawing.PointF -37, 19),
            (New-Object Drawing.PointF -32, 17)
        )
        $g.FillPolygon($moonBrush, $dPts)
        $g.DrawPolygon($leadPen, $dPts)
    }

    $tailBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(18, 92, 104))
    $tailPath = New-Object Drawing.Drawing2D.GraphicsPath
    $tailPath.AddBezier(-10, 22, -13, 34, -14, 43, -8, 45)
    $tailPath.AddBezier(-8, 45, -3, 49, 3, 49, 8, 45)
    $tailPath.AddBezier(8, 45, 13, 43, 12, 34, 9, 22)
    $tailPath.CloseFigure()
    $g.FillPath($tailBrush, $tailPath)
    $g.DrawPath($leadPen, $tailPath)
    $g.DrawLine($leadPen, -4, 25, -5, 46)
    $g.DrawLine($leadPen, 0, 26, 0, 48)
    $g.DrawLine($leadPen, 4, 25, 5, 46)

    $beakBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(68, 72, 84))
    $beakPath = New-Object Drawing.Drawing2D.GraphicsPath
    $beakPath.AddBezier(9, -28, 17, -28, 23, -25, 26, -21)
    $beakPath.AddBezier(26, -21, 19, -20, 14, -20, 9, -21)
    $beakPath.CloseFigure()
    $g.FillPath($beakBrush, $beakPath)
    $g.DrawPath($leadPen, $beakPath)

    $headBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(14, 14, 20))
    $headPath = New-Object Drawing.Drawing2D.GraphicsPath
    $headPath.AddBezier(-12, -14, -10, -27, -2, -32, 6, -30)
    $headPath.AddBezier(6, -30, 11, -29, 12, -23, 9, -20)
    $headPath.AddBezier(9, -20, 8, -16, 10, -13, 12, -10)
    $headPath.AddBezier(12, -10, 4, -8, -4, -8, -12, -14)
    $headPath.CloseFigure()
    $g.FillPath($headBrush, $headPath)
    $g.DrawPath($leadPen, $headPath)

    $wingPurple = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(74, 22, 118))
    $wingPlum   = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(104, 20, 64))
    $bodyPath = New-Object Drawing.Drawing2D.GraphicsPath
    $bodyPath.AddBezier(-12, -14, -22, -4, -20, 16, -6, 29)
    $bodyPath.AddBezier(-6, 29, -2, 33, 2, 33, 5, 29)
    $bodyPath.AddBezier(5, 29, 18, 16, 20, -2, 12, -10)
    $bodyPath.AddBezier(12, -10, 4, -12, -4, -12, -12, -14)
    $bodyPath.CloseFigure()
    $g.FillPath($wingPurple, $bodyPath)

    $mantle = New-Object Drawing.Drawing2D.GraphicsPath
    $mantle.AddBezier(-12, -14, -4, -12, 4, -12, 12, -10)
    $mantle.AddBezier(12, -10, 6, -3, 2, -1, 0, 2)
    $mantle.AddBezier(0, 2, -3, -1, -7, -4, -12, -14)
    $mantle.CloseFigure()
    $g.FillPath($wingPlum, $mantle)
    $g.DrawPath($leadPen, $mantle)

    $g.DrawPath($leadPen, $bodyPath)
    $g.DrawBezier($leadPen, 0, 2, -2, 14, -1, 22, 2, 31)
    $g.DrawBezier($leadPen, -15, -2, -10, 6, -4, 8, -1, 4)
    $g.DrawBezier($leadPen, 15, 0, 10, 7, 4, 9, 0, 4)
    $g.DrawBezier($leadPen, -14, 10, -9, 17, -4, 18, -1, 13)
    $g.DrawBezier($leadPen, 14, 11, 9, 18, 4, 19, 0, 14)

    $ringBrush.Dispose(); $coreBrush.Dispose(); $moonBrush.Dispose(); $starBrush.Dispose()
    $tailBrush.Dispose(); $beakBrush.Dispose(); $headBrush.Dispose(); $wingPurple.Dispose(); $wingPlum.Dispose()
    $moonPath.Dispose(); $tailPath.Dispose(); $beakPath.Dispose(); $headPath.Dispose(); $bodyPath.Dispose(); $mantle.Dispose()
    $leadPen.Dispose()
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# .ico with PNG-compressed entries
$sizes = 16, 20, 24, 32, 48, 64, 256
$pngs = @(foreach ($s in $sizes) { , (New-IconPng $s) })
$w = New-Object IO.BinaryWriter ([IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $w.Write([byte]($sizes[$i] % 256)); $w.Write([byte]($sizes[$i] % 256))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Close()

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Management.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll',
        'Microsoft.VisualBasic.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'Microsoft.CSharp.dll', 'System.ServiceProcess.dll', 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll' |
    ForEach-Object { "/r:$fw\$_" }
$resources = 'Theme.xaml', 'MainWindow.xaml', 'vpn.ico' | ForEach-Object { "/resource:$(Join-Path $src $_),VpnPanel.$_" }
$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }

# The app
if ($Target -ne 'Setup') {
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /main:VpnPanel.Program "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" `
        $refs $resources $sources
    if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
    "Built $out"
}

# The installer: same sources with the app embedded (compressed to eliminate raw PE dropper signatures), its own entry point and an admin manifest
if ($Target -ne 'App') {
    if (-not (Test-Path $out)) { throw "$out not found: build the app first (-Target App)" }
    $payload = Join-Path $dist 'app.payload'
    $fs = [System.IO.File]::Create($payload)
    $gz = New-Object System.IO.Compression.GZipStream ($fs, [System.IO.Compression.CompressionMode]::Compress)
    $raw = [System.IO.File]::ReadAllBytes($out)
    $gz.Write($raw, 0, $raw.Length)
    $gz.Close()
    $fs.Close()

    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /main:VpnPanel.SetupProgram "/out:$setup" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'setup.manifest')" `
        $refs $resources "/resource:$payload,VpnPanel.app.payload" $sources
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
    "Built $setup"
}