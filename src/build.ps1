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

# App icon: white raven outline on a purple disc
function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s / 32.0, $s / 32.0)
    $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(139, 92, 246))
    $g.FillEllipse($brush, 0.5, 0.5, 31, 31)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White, 1.9)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddLine(21.2, 11.2, 26.2, 10.0)
    $p.AddLine(26.2, 10.0, 21.5, 8.5)
    $p.AddBezier(21.5, 8.5, 19.2, 6.4, 15.8, 6.8, 14.5, 9.8)
    $p.AddBezier(14.5, 9.8, 11.2, 12.0, 8.5, 16.5, 6.2, 23.2)
    $p.AddLine(6.2, 23.2, 9.6, 23.8)
    $p.AddLine(9.6, 23.8, 12.2, 20.2)
    $p.AddBezier(12.2, 20.2, 17.5, 20.8, 21.8, 16.5, 21.2, 11.2)
    $p.CloseFigure()
    $g.DrawPath($pen, $p)
    $wing = New-Object Drawing.Drawing2D.GraphicsPath
    $wing.AddBezier(15.2, 12.2, 16.8, 14.5, 15.0, 17.8, 11.0, 19.2)
    $g.DrawPath($pen, $wing)
    $g.DrawLine($pen, 15.5, 20.2, 15.5, 24.8)
    $g.DrawLine($pen, 14.0, 24.8, 17.8, 24.8)
    $g.FillEllipse([Drawing.Brushes]::White, 18.2, 8.4, 1.7, 1.7)
    $p.Dispose(); $wing.Dispose(); $pen.Dispose(); $brush.Dispose()
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