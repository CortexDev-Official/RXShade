<#
.SYNOPSIS
    Builds a multi-resolution Windows .ico from a single PNG.

.DESCRIPTION
    RXShade ships one source image (RXShade-Icon.png). This script resamples it
    to the sizes Windows actually requests (16-256 px) and packs them into a
    single .ico, so Explorer, the taskbar and the installer all pick a crisp
    bitmap instead of scaling one large image.

.EXAMPLE
    .\tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\..\RXShade-Icon.png",
    [string]$Output = "$PSScriptRoot\..\src\RXShade\RXShade.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = (Resolve-Path -LiteralPath $Source).Path
$output = [System.IO.Path]::GetFullPath($Output)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null

# Sizes Windows asks for. 256 is stored as 0 in the ICONDIRENTRY byte.
$sizes = 16, 24, 32, 48, 64, 128, 256

$image = [System.Drawing.Image]::FromFile($source)
$pngData = New-Object System.Collections.Generic.List[byte[]]

try
{
    foreach ($size in $sizes)
    {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try
        {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($image, 0, 0, $size, $size)
        }
        finally
        {
            $graphics.Dispose()
        }

        $stream = New-Object System.IO.MemoryStream
        try
        {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngData.Add($stream.ToArray())
        }
        finally
        {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally
{
    $image.Dispose()
}

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter($file)

try
{
    # ICONDIR
    $writer.Write([UInt16]0)              # reserved
    $writer.Write([UInt16]1)              # type: icon
    $writer.Write([UInt16]$sizes.Count)   # image count

    # ICONDIRENTRY per image
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++)
    {
        $size = $sizes[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }

        $writer.Write([Byte]$dimension)       # width
        $writer.Write([Byte]$dimension)       # height
        $writer.Write([Byte]0)                # palette colours
        $writer.Write([Byte]0)                # reserved
        $writer.Write([UInt16]1)              # colour planes
        $writer.Write([UInt16]32)             # bits per pixel
        $writer.Write([UInt32]$pngData[$i].Length)
        $writer.Write([UInt32]$offset)

        $offset += $pngData[$i].Length
    }

    foreach ($png in $pngData)
    {
        $writer.Write($png)
    }
}
finally
{
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Wrote $output ($($sizes -join ', ') px)"
