Add-Type -AssemblyName System.Drawing
# Resolve relative to this script so it works from any checkout location.
$assetDir = Join-Path $PSScriptRoot 'src\SelectionAssistant.App\Assets'
$src = Join-Path $assetDir 'app-icon.png'
$dst = Join-Path $assetDir 'app-icon.ico'

# Downscale from 1254x1254 to 256x256 master, then generate each size from it
# for crisp small icons (a 16x16 sampled straight from 1254 is muddy).
$srcBmp = [System.Drawing.Bitmap]::new((Resolve-Path $src).Path)
$master = [System.Drawing.Bitmap]::new(256, 256)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.DrawImage($srcBmp, 0, 0, 256, 256)
$g.Dispose()
$srcBmp.Dispose()

# Encode each size to PNG bytes (PNG-in-ICO, preserves alpha/transparency).
$sizes = @(256, 128, 64, 48, 32, 16)
$pngBytesList = @()
foreach ($s in $sizes) {
    $bmp = if ($s -eq 256) { $master } else {
        $b = [System.Drawing.Bitmap]::new($s, $s)
        $gg = [System.Drawing.Graphics]::FromImage($b)
        $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $gg.DrawImage($master, 0, 0, $s, $s)
        $gg.Dispose()
        $b
    }
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytesList += ,$ms.ToArray()
    $ms.Dispose()
    if ($s -ne 256) { $bmp.Dispose() }
}
$master.Dispose()

# Write ICO container.
$out = [System.IO.File]::Create($dst)
$bw = [System.IO.BinaryWriter]::new($out)
# ICONDIR
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type = icon
$bw.Write([uint16]$sizes.Count)
# ICONDIRENTRY for each
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $h = $w
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0)    # color palette
    $bw.Write([byte]0)    # reserved
    $bw.Write([uint16]1)  # color planes
    $bw.Write([uint16]32) # bits per pixel
    $bw.Write([uint32]$pngBytesList[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngBytesList[$i].Length
}
# image data
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngBytesList[$i])
}
$bw.Flush()
$bw.Dispose()
$out.Dispose()

$info = Get-Item $dst
Write-Host ("ICO written: {0} bytes ({1} sizes)" -f $info.Length, $sizes.Count)
