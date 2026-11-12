# Generate-Icons.ps1: 从 icons/*.png 生成多尺寸合一的 .ico 文件
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspaceRoot = Split-Path -Parent $scriptDir
$iconsSourceDir = Join-Path $workspaceRoot "icons"
$outputIconsDir = Join-Path $workspaceRoot "bin\icons"

if (-not (Test-Path $outputIconsDir)) {
    New-Item -ItemType Directory -Force -Path $outputIconsDir | Out-Null
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)

function Convert-PngToMultiSizeIco {
    param(
        [string]$sourcePngPath,
        [string]$outputIcoPath
    )

    $srcImg = [System.Drawing.Image]::FromFile($sourcePngPath)

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
    $srcImg.Dispose()

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

Write-Host "正在从 $iconsSourceDir 生成多尺寸 ICO 文件至 $outputIconsDir ..."

$pngFiles = Get-ChildItem -Path $iconsSourceDir -Filter "*.png"
foreach ($png in $pngFiles) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($png.Name)
    $icoName = "$base.ico"
    $targetIco = Join-Path $outputIconsDir $icoName
    Convert-PngToMultiSizeIco -sourcePngPath $png.FullName -outputIcoPath $targetIco
    Write-Host "  已生成: $icoName (含 16~256px)"
}

Write-Host "[OK] 全部图标生成完毕！"
