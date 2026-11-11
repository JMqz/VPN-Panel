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

# App icon: Solid Accent Medallion with Raven Cutout (Mullvad style for maximum clarity across all sizes)
function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)

    $inset = if ($s -le 20) { 0.5 } else { 1.0 }
    $bgBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(68, 173, 77))
    $g.FillEllipse($bgBrush, $inset, $inset, $s - 2 * $inset, $s - 2 * $inset)
    $bgBrush.Dispose()

    $cx = $s / 2.0; $cy = $s / 2.0
    $g.TranslateTransform($cx - 0.5, $cy)
    $scale = $s / 115.0
    $g.ScaleTransform($scale, $scale)

    $ravenBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(16, 28, 44))
    $tailBrush = $ravenBrush

    # Beak
    $bp = New-Object Drawing.Drawing2D.GraphicsPath
    $bp.AddBezier(9, -24, 19, -24, 27, -21, 31, -15)
    $bp.AddBezier(31, -15, 22, -14, 15, -14, 9, -15)
    $bp.CloseFigure()
    $g.FillPath($ravenBrush, $bp)

    # Head & Neck
    $hp = New-Object Drawing.Drawing2D.GraphicsPath
    $hp.AddBezier(-16, -10, -14, -26, -5, -31, 5, -29)
    $hp.AddBezier(5, -29, 11, -28, 12, -21, 9, -17)
    $hp.AddBezier(9, -17, 8, -12, 10, -8, 12, -5)
    $hp.AddBezier(12, -5, 2, -4, -7, -4, -16, -10)
    $hp.CloseFigure()
    $g.FillPath($ravenBrush, $hp)

    # Body & Folded Wings
    $wp = New-Object Drawing.Drawing2D.GraphicsPath
    $wp.AddBezier(-16, -10, -28, 2, -26, 26, -9, 41)
    $wp.AddBezier(-9, 41, -4, 46, 1, 46, 5, 41)
    $wp.AddBezier(5, 41, 22, 26, 25, 4, 15, -5)
    $wp.AddBezier(15, -5, 5, -8, -5, -8, -16, -10)
    $wp.CloseFigure()
    $g.FillPath($ravenBrush, $wp)

    # Tail
    $tp = New-Object Drawing.Drawing2D.GraphicsPath
    $tp.AddBezier(-12, 33, -15, 47, -17, 57, -10, 60)
    $tp.AddBezier(-10, 60, -4, 64, 3, 64, 9, 60)
    $tp.AddBezier(9, 60, 15, 57, 14, 47, 10, 33)
    $tp.CloseFigure()
    $g.FillPath($tailBrush, $tp)

    $ravenBrush.Dispose(); $tailBrush.Dispose()
    $bp.Dispose(); $hp.Dispose(); $wp.Dispose(); $tp.Dispose()
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
    if (Test-Path $out) {
        try { Remove-Item $out -Force -ErrorAction Stop }
        catch { Rename-Item $out "$out.$([Guid]::NewGuid().ToString('N')).old" -Force -ErrorAction SilentlyContinue }
    }
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /main:VpnPanel.Program "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" `
        $refs $resources $sources
    if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
    "Built $out"
}

# The installer: same sources with the app embedded (compressed to eliminate raw PE dropper signatures), its own entry point and an admin manifest
if ($Target -ne 'App') {
    if (-not (Test-Path $out)) { throw "$out not found: build the app first (-Target App)" }
    if (Test-Path $setup) {
        try { Remove-Item $setup -Force -ErrorAction Stop }
        catch { Rename-Item $setup "$setup.$([Guid]::NewGuid().ToString('N')).old" -Force -ErrorAction SilentlyContinue }
    }
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