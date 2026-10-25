# Builds ..\dist\VpnPanel.exe and the installer ..\dist\VPN Panel Setup.exe from the .cs/.xaml files here,
# with the C# compiler that ships with Windows (.NET Framework 4.8).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File C:\VPN\src\build.ps1

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path (Split-Path $src) 'dist'
$out = Join-Path $dist 'VpnPanel.exe'
$setup = Join-Path $dist 'VPN Panel Setup.exe'
New-Item -ItemType Directory -Force $dist | Out-Null
$ico = Join-Path $src 'vpn.ico'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Add-Type -AssemblyName System.Drawing

# App icon: white shield on a green disc
function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s / 32.0, $s / 32.0)
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(46, 160, 67))), 0.5, 0.5, 31, 31)
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddLine(16, 6, 24, 9)
    $p.AddLine(24, 9, 24, 15)
    $p.AddBezier(24, 15, 24, 20, 20, 23.5, 16, 26)
    $p.AddBezier(16, 26, 12, 23.5, 8, 20, 8, 15)
    $p.AddLine(8, 15, 8, 9)
    $p.CloseFigure()
    $g.FillPath([Drawing.Brushes]::White, $p)
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
        'Microsoft.VisualBasic.dll', 'Microsoft.CSharp.dll', 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll' |
    ForEach-Object { "/r:$fw\$_" }
$resources = 'Theme.xaml', 'MainWindow.xaml', 'vpn.ico' | ForEach-Object { "/resource:$(Join-Path $src $_),VpnPanel.$_" }
$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }

# The app
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /main:VpnPanel.Program "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" `
    $refs $resources $sources
if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
"Built $out"

# The installer: same sources with the app embedded, its own entry point and an admin manifest
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /main:VpnPanel.SetupProgram "/out:$setup" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'setup.manifest')" `
    $refs $resources "/resource:$out,VpnPanel.VpnPanel.exe" $sources
if ($LASTEXITCODE) { throw "csc failed ($LASTEXITCODE)" }
"Built $setup"