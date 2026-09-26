# Generates assets/logo.ico from assets/logo.png (multi-size, PNG-compressed 256px frame)
# Invoked automatically before build via csproj target. Skips if ico is newer than png.
param([string]$ProjectDir)

Add-Type -AssemblyName System.Drawing

$png = Join-Path $ProjectDir 'assets\logo.png'
$ico = Join-Path $ProjectDir 'assets\logo.ico'

if (-not (Test-Path $png)) { Write-Host "mkico: logo.png not found, skip"; exit 0 }
if ((Test-Path $ico) -and ((Get-Item $ico).LastWriteTime -ge (Get-Item $png).LastWriteTime)) { exit 0 }

$src = New-Object System.Drawing.Bitmap($png)
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type: icon
$bw.Write([UInt16]$sizes.Count) # count

$frames = @()
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($src, $s, $s)
    $msImg = New-Object System.IO.MemoryStream
    $bmp.Save($msImg, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $msImg.ToArray()
    $frames += , $bytes
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)   # width, height (0 = 256)
    $bw.Write([Byte]0); $bw.Write([Byte]0)         # palette, reserved
    $bw.Write([UInt16]1)                           # planes
    $bw.Write([UInt16]32)                          # bpp
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
    $msImg.Dispose(); $bmp.Dispose()
}
foreach ($b in $frames) { $bw.Write($b) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($ico, $ms.ToArray())
$bw.Dispose(); $ms.Dispose(); $src.Dispose()
Write-Host "mkico: generated $ico"
exit 0
