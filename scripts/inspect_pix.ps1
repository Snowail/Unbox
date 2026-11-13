Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\icons\zip.png"))
for ($y = 250; $y -lt 280; $y += 5) {
    for ($x = 240; $x -lt 270; $x += 5) {
        $c = $img.GetPixel($x, $y)
        Write-Host "Pixel ($x, $y): R=$($c.R), G=$($c.G), B=$($c.B), A=$($c.A)"
    }
}
$img.Dispose()
