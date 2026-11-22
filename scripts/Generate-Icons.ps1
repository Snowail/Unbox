[CmdletBinding()]
param(
    [string]$SourceDir,
    [string]$OutputDir,
    [string]$Catalog
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root 'icons' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'bin\icons' }
if (-not $Catalog) { $Catalog = Join-Path $root 'assets\archive-formats.json' }
$data = Get-Content -LiteralPath $Catalog -Raw -Encoding UTF8 | ConvertFrom-Json
if ($data.schemaVersion -ne 1 -or -not $data.formats) { throw 'Invalid format catalog' }
$keys = @{ origin = 'ORIGIN' }
foreach ($item in $data.formats) {
    if ($item.iconKey -cnotmatch '^[a-z0-9]+$') { throw "Invalid icon key: $($item.iconKey)" }
    $keys[$item.iconKey] = $item.badgeLabel
}
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDir)) | Out-Null
$sources = @{}
foreach ($file in Get-ChildItem -LiteralPath $SourceDir -File -Filter '*.png') {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    if ($sources.ContainsKey($file.BaseName)) { throw "Duplicate icon source: $($file.BaseName)" }
    $sources[$file.BaseName] = $file.FullName
}
$sizes = @(256, 128, 64, 48, 32, 24, 16)

function Convert-PngToMultiSizeIco {
    param(
        [System.Drawing.Image]$srcImg,
        [string]$outputIcoPath
    )


    $pngDataList = [System.Collections.Generic.List[byte[]]]::new()
    $dimList = [System.Collections.Generic.List[int]]::new()

    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($srcImg, 0, 0, $s, $s)
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        $pngDataList.Add($ms.ToArray())
        $dimList.Add($s)
        $ms.Dispose()
    }

    # 组装 ICO 文件
    $fs = [System.IO.File]::Create($outputIcoPath)
    $bw = New-Object System.IO.BinaryWriter $fs

    # 1. ICONDIR Header (6 bytes)
    $bw.Write([uint16]0) # Reserved
    $bw.Write([uint16]1) # Type (1 = ICO)
    $bw.Write([uint16]$pngDataList.Count) # Count

    # 计算数据偏移
    $offset = 6 + ($pngDataList.Count * 16)

    # 2. ICONDIRENTRY (16 bytes each)
    for ($i = 0; $i -lt $pngDataList.Count; $i++) {
        $dim = $dimList[$i]
        $bWidth = if ($dim -ge 256) { 0 } else { [byte]$dim }
        $bHeight = if ($dim -ge 256) { 0 } else { [byte]$dim }
        $bytes = $pngDataList[$i]

        $bw.Write([byte]$bWidth)
        $bw.Write([byte]$bHeight)
        $bw.Write([byte]0)   # ColorCount
        $bw.Write([byte]0)   # Reserved
        $bw.Write([uint16]1) # Color Planes
        $bw.Write([uint16]32)# Bits per pixel
        $bw.Write([uint32]$bytes.Length) # Image size
        $bw.Write([uint32]$offset)       # Image offset

        $offset += $bytes.Length
    }

    # 3. 写入各尺寸 PNG 数据
    for ($i = 0; $i -lt $pngDataList.Count; $i++) {
        $bw.Write($pngDataList[$i])
    }

    $bw.Flush()
    $bw.Close()
    $fs.Close()
}

function Render-MissingBadge([Drawing.Image]$BaseImage, [string]$Label) {
    $bitmap = [Drawing.Bitmap]::new(512, 512, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $format = [Drawing.StringFormat]::new()
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(30, 41, 59))
    $font = $null
    try {
        $graphics.DrawImage($BaseImage, 0, 0, 512, 512)
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $rect = [Drawing.RectangleF]::new(48, 112, 268, 150)
        $format.Alignment = $format.LineAlignment = [Drawing.StringAlignment]::Center
        for ($size = 92; $size -ge 18; $size -= 2) {
            if ($font) { $font.Dispose() }
            $font = [Drawing.Font]::new('Segoe UI', $size, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
            if ($graphics.MeasureString($Label, $font).Width -lt 248) { break }
        }
        $graphics.DrawString($Label, $font, $brush, $rect, $format)
        return $bitmap
    } catch {
        $bitmap.Dispose()
        throw
    } finally {
        if ($font) { $font.Dispose() }
        $brush.Dispose()
        $format.Dispose()
        $graphics.Dispose()
    }
}

foreach ($key in ($keys.Keys | Sort-Object)) {
    $source = $sources[$key]
    if (-not $source -and -not $sources['origin']) {
        throw "Missing source for icon $key and blank template origin.png"
    }
    $path = if ($source) { $source } else { $sources['origin'] }
    $image = [Drawing.Image]::FromFile($path)
    $badge = $null
    $target = Join-Path $OutputDir "$key.ico"
    $temporary = "$target.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        if ($image.Width -ne 512 -or $image.Height -ne 512) { throw "Unexpected icon dimensions: $path" }
        if (-not $source) {
            $badge = Render-MissingBadge $image $keys[$key]
            $aliases = ($data.formats | Where-Object iconKey -eq $key).suffix -join ', '
            Write-Host "Generated badge $key for $aliases using $path"
        }
        $inputImage = if ($badge) { $badge } else { $image }
        Convert-PngToMultiSizeIco $inputImage $temporary
        if ([IO.File]::Exists($target)) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $target) }
    } finally {
        if ($badge) { $badge.Dispose() }
        $image.Dispose()
        [IO.File]::Delete($temporary)
    }
}
$unused = @($sources.Keys | Where-Object { -not $keys.ContainsKey($_) } | Sort-Object)
if ($unused.Count) { Write-Host "Unused artwork: $($unused -join ', ')" }
