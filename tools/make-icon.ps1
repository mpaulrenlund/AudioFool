<#
.SYNOPSIS
    Builds a multi-resolution Windows .ico from a photo.

.DESCRIPTION
    Crops a square region out of a source image and writes a PNG-compressed .ico
    containing every size Windows asks for - 16px in a list view up to 256px on the
    desktop at large-icon settings. Windows picks the closest frame instead of
    scaling one badly.

    Crop position and size are given as fractions of the source width/height, so the
    same numbers work whatever resolution the photo is.

    PNG-compressed icons need Vista or newer, which is a safe assumption here.

.EXAMPLE
    .\make-icon.ps1 -Source cat.jpg -Out ..\src\AudioFool\Resources\AudioFool.ico `
                    -Left 0.02 -Top 0.165 -Size 0.47
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Out,

    # Crop window, as fractions. Left/Top are of width/height; Size is of width.
    [double] $Left = 0.02,
    [double] $Top = 0.165,
    [double] $Size = 0.47,

    # Emit the cropped square as a PNG too, so the framing can be eyeballed.
    [string] $PreviewPath,

    [int[]] $Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$src = [System.Drawing.Image]::FromFile($Source)

try {
    Write-Host "source        : $Source ($($src.Width)x$($src.Height))"

    # Phone photos store the sensor's pixels and an EXIF orientation tag saying how
    # to turn them. Every image viewer honours that tag; System.Drawing does not. So
    # a "portrait" photo can arrive as landscape pixels, and crop fractions would
    # then refer to the wrong axes entirely. Rotate first.
    $orientationId = 0x0112
    if ($src.PropertyIdList -contains $orientationId) {
        $orientation = $src.GetPropertyItem($orientationId).Value[0]
        $transform = switch ($orientation) {
            2 { [System.Drawing.RotateFlipType]::RotateNoneFlipX }
            3 { [System.Drawing.RotateFlipType]::Rotate180FlipNone }
            4 { [System.Drawing.RotateFlipType]::RotateNoneFlipY }
            5 { [System.Drawing.RotateFlipType]::Rotate90FlipX }
            6 { [System.Drawing.RotateFlipType]::Rotate90FlipNone }
            7 { [System.Drawing.RotateFlipType]::Rotate270FlipX }
            8 { [System.Drawing.RotateFlipType]::Rotate270FlipNone }
            default { $null }
        }

        if ($transform) {
            $src.RotateFlip($transform)
            $src.RemovePropertyItem($orientationId)
            Write-Host "orientation   : EXIF $orientation -> $transform, now $($src.Width)x$($src.Height)"
        }
    }

    $cropSize = [int][Math]::Round($src.Width * $Size)
    $cropX = [int][Math]::Round($src.Width * $Left)
    $cropY = [int][Math]::Round($src.Height * $Top)

    # Keep the square inside the image no matter what fractions were passed.
    $cropSize = [Math]::Min($cropSize, [Math]::Min($src.Width, $src.Height))
    $cropX = [Math]::Max(0, [Math]::Min($cropX, $src.Width - $cropSize))
    $cropY = [Math]::Max(0, [Math]::Min($cropY, $src.Height - $cropSize))

    Write-Host "crop          : ${cropSize}x${cropSize} at $cropX,$cropY"

    $square = New-Object System.Drawing.Bitmap $cropSize, $cropSize
    $g = [System.Drawing.Graphics]::FromImage($square)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage(
        $src,
        (New-Object System.Drawing.Rectangle 0, 0, $cropSize, $cropSize),
        (New-Object System.Drawing.Rectangle $cropX, $cropY, $cropSize, $cropSize),
        [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    if ($PreviewPath) {
        $square.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "preview       : $PreviewPath"
    }

    # Render each size from the full-resolution crop rather than from the previous
    # (smaller) one, so no downscaling error compounds.
    $frames = foreach ($s in ($Sizes | Sort-Object)) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $gg = [System.Drawing.Graphics]::FromImage($bmp)
        $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $gg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $gg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $gg.DrawImage($square, 0, 0, $s, $s)
        $gg.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose(); $bmp.Dispose()

        [pscustomobject]@{ Size = $s; Bytes = $bytes }
    }

    $square.Dispose()

    # --- assemble the .ico ---------------------------------------------------
    # ICONDIR: reserved(2) type(2) count(2), then one 16-byte ICONDIRENTRY per
    # frame, then the frame payloads.
    $outDir = Split-Path -Parent $Out
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    }

    $stream = [System.IO.File]::Create($Out)
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([uint16]0)                  # reserved
        $writer.Write([uint16]1)                  # 1 = icon
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)
        foreach ($f in $frames) {
            # 256 is stored as 0 in a single byte.
            $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
            $writer.Write([byte]$dim)             # width
            $writer.Write([byte]$dim)             # height
            $writer.Write([byte]0)                # palette size (0 = no palette)
            $writer.Write([byte]0)                # reserved
            $writer.Write([uint16]1)              # colour planes
            $writer.Write([uint16]32)             # bits per pixel
            $writer.Write([uint32]$f.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $f.Bytes.Length
        }

        foreach ($f in $frames) {
            $writer.Write($f.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    $written = Get-Item $Out
    Write-Host "icon          : $($written.FullName) ($([math]::Round($written.Length / 1KB, 1)) KB)"
    Write-Host "frames        : $(($frames.Size) -join ', ')"
}
finally {
    $src.Dispose()
}
