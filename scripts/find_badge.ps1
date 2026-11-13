Add-Type -AssemblyName System.Drawing

$orig = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\icons\origin.png"))
$zip = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\icons\zip.png"))

$minX = 9999; $maxX = -1
$minY = 9999; $maxY = -1

for ($y = 0; $y -lt 512; $y++) {
    for ($x = 0; $x -lt 512; $x++) {
        $c1 = $orig.GetPixel($x, $y)
        $c2 = $zip.GetPixel($x, $y)
        if ($c1.ToArgb() -ne $c2.ToArgb()) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

$orig.Dispose()
$zip.Dispose()

Write-Host "Badge bounding box: X=[$minX, $maxX], Y=[$minY, $maxY], W=$($maxX - $minX + 1), H=$($maxY - $minY + 1)"
