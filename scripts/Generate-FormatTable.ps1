[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Catalog,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $Catalog -Raw -Encoding UTF8 | ConvertFrom-Json
if ($data.schemaVersion -ne 1 -or -not $data.formats) { throw 'Invalid archive format catalog schema' }

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('#pragma once')
$lines.Add('// Generated from assets/archive-formats.json.')
$lines.Add('inline constexpr unsigned ArchiveFormatSchemaVersion = 1;')
$hash = (Get-FileHash -LiteralPath $Catalog -Algorithm SHA256).Hash
$lines.Add("inline constexpr char ArchiveFormatCatalogSha256[] = `"$hash`";")
$lines.Add('inline constexpr ArchiveFormatEntry ArchiveFormatTable[] = {')
foreach ($item in ($data.formats | Sort-Object @{Expression={$_.suffix.Length};Descending=$true}, suffix)) {
    if ($item.suffix -cnotmatch '^\.[a-z0-9]+(?:\.[a-z0-9]+)*$' -or -not $seen.Add($item.suffix) -or
        $item.iconKey -cnotmatch '^[a-z0-9]+$' -or $item.kind -cnotin @('Archive','Stream','TarWrapper','Split') -or
        $item.associate -isnot [bool] -or ($item.associate -and $item.suffix -notmatch '^\.[a-z0-9]+$') -or
        $item.displayName -isnot [string] -or [string]::IsNullOrWhiteSpace($item.displayName) -or
        $item.badgeLabel -isnot [string] -or [string]::IsNullOrWhiteSpace($item.badgeLabel) -or
        $item.pickerGroup -cnotin @('Archive','Stream') -or
        -not $item.handlerHints -or @($item.handlerHints -cnotmatch '^[A-Za-z0-9]+$').Count) {
        throw "Invalid archive format catalog entry: $($item.suffix)"
    }
    $hints = $item.handlerHints -join ' '
    $lines.Add(('    {{L"{0}", ArchiveKind::{1}, L"{2}"}},' -f $item.suffix, $item.kind, $hints))
}
$lines.Add('};')
$destination = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$temporary = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllLines($temporary, $lines, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($destination)) {
        [IO.File]::Replace($temporary, $destination, [NullString]::Value)
    } else {
        [IO.File]::Move($temporary, $destination)
    }
} finally {
    [IO.File]::Delete($temporary)
}
