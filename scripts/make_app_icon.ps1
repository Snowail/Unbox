[CmdletBinding()]
param(
    [string]$Output,
    [string]$Renderer = $env:UNBOX_RESVG
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'src\UnzipProxy\obj\UnboxBrand.ico' }
if (-not $Renderer) {
    $command = Get-Command resvg.exe -ErrorAction SilentlyContinue
    $Renderer = if ($command) { $command.Source } else { Join-Path $root '.agent-tmp\resvg\resvg.exe' }
}
if (-not (Test-Path -LiteralPath $Renderer -PathType Leaf) -or
    ((& $Renderer --version) -join '').Trim() -notmatch '^(?:resvg )?0\.47\.0$' -or $LASTEXITCODE -ne 0) {
    throw 'Install resvg 0.47.0 and set UNBOX_RESVG to its resvg.exe.'
}

$Output = [IO.Path]::GetFullPath($Output)
$directory = [IO.Path]::GetDirectoryName($Output)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$token = [Guid]::NewGuid().ToString('N')
$png = Join-Path $directory "UnboxBrand.$token.png"
$ico = Join-Path $directory "UnboxBrand.$token.ico"
$sizes = @(16,24,32,48,64,128,256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $name = if ($size -le 24) { 'package-box-24px.svg' } else { 'package-box.svg' }
        & $Renderer --width $size --height $size (Join-Path $root "appicon\$name") $png
        if ($LASTEXITCODE -ne 0) { throw "resvg failed for $name at $size px" }
        $frames.Add([IO.File]::ReadAllBytes($png))
        [IO.File]::Delete($png)
    }

    $writer = [IO.BinaryWriter]::new([IO.File]::Open($ico, [IO.FileMode]::CreateNew))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; ++$i) {
            $dim = [byte]($sizes[$i] % 256)
            $writer.Write($dim)
            $writer.Write($dim)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose() }

    if ([IO.File]::Exists($Output)) { [IO.File]::Replace($ico, $Output, [NullString]::Value) }
    else { [IO.File]::Move($ico, $Output) }
} finally {
    [IO.File]::Delete($png)
    [IO.File]::Delete($ico)
}
