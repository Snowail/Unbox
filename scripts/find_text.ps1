Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\icons\zip.png"))
# The label is in the lower center. Let's find white/near-white pixels in bottom half
$minX = 9999; $maxX = -1
$minY = 9999; $maxY = -1
for ($y = 200; $y -lt 450; $y++) {
    for ($x = 100; $x -lt 400; $x++) {
        $c = $img.GetPixel($x, $y)
        if ($c.R -gt 240 -and $c.G -gt 240 -and $c.B -gt 240 -and $c.A -gt 200) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
$img.Dispose()
Write-Host "Label text bounds: X=[$minX, $maxX], Y=[$minY, $maxY], W=$($maxX - $minX + 1), H=$($maxY - $minY + 1)"
