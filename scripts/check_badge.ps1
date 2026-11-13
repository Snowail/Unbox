Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\icons\zip.png"))
# Check non-transparent pixels in bottom-right / center where text is located
Write-Host "Sampled zip.png successfully"
$img.Dispose()
