# make_app_icon.ps1: 生成 unbox 应用程序图标 app.ico
Add-Type -AssemblyName System.Drawing

$srcPng = "icon.png"
$targetIco1 = "src\UnzipProxy\app.ico"
$targetIco2 = "bin\app.ico"
$sizes = @(256, 128, 64, 48, 32, 24, 16)

$img = [System.Drawing.Image]::FromFile($srcPng)
$pngDataList = [System.Collections.Generic.List[byte[]]]::new()
$dimList = [System.Collections.Generic.List[int]]::new()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($img, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngDataList.Add($ms.ToArray())
    $dimList.Add($s)
    $ms.Dispose()
}
$img.Dispose()

function Save-Ico($path) {
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$pngDataList.Count)
    $offset = 6 + ($pngDataList.Count * 16)

    for ($i = 0; $i -lt $pngDataList.Count; $i++) {
        $dim = $dimList[$i]
        $w = if ($dim -ge 256) { [byte]0 } else { [byte]$dim }
        $h = if ($dim -ge 256) { [byte]0 } else { [byte]$dim }
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$pngDataList[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $pngDataList[$i].Length
    }
    for ($i = 0; $i -lt $pngDataList.Count; $i++) {
        $bw.Write($pngDataList[$i])
    }
    $bw.Flush()
    $bw.Close()
    $fs.Close()
}

Save-Ico $targetIco1
Save-Ico $targetIco2
Write-Host "[OK] app.ico successfully created in $targetIco1 and $targetIco2"
