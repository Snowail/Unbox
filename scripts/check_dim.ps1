Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot "..\icons\origin.png"))
$w = $img.Width
$h = $img.Height
Write-Host "origin.png size: ${w}x${h}"
$img.Dispose()
