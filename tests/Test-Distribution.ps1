[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [switch]$VerifyAssociations
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Drawing
$fixed = 'D480E5F7ACF03183DC8CD9FE341A06D34E768B3C6606C0DB4AEC2A7D0EB5098E'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repo 'assets\7z.dll')) {
    foreach ($path in @('assets\7z.dll','bin\7z.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $repo $path) -Algorithm SHA256).Hash -ne $fixed) {
            throw "Unexpected DLL SHA-256: $path"
        }
    }
}

function Test-IcoFrameTable([byte[]]$Bytes, [string]$Name) {
    if ($Bytes.Length -lt 6 -or [BitConverter]::ToUInt16($Bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($Bytes, 2) -ne 1) { throw "Invalid ICO header: $Name" }
    $count = [BitConverter]::ToUInt16($Bytes, 4)
    $last = 6 + 16 * $count
    if ($last -gt $Bytes.Length) { throw "Invalid ICO directory: $Name" }
    $sizes = @()
    for ($i = 0; $i -lt $count; ++$i) {
        $entry = 6 + 16 * $i
        $size = if ($Bytes[$entry]) { [int]$Bytes[$entry] } else { 256 }
        $height = if ($Bytes[$entry+1]) { [int]$Bytes[$entry+1] } else { 256 }
        $length = [BitConverter]::ToUInt32($Bytes, $entry + 8)
        $offset = [BitConverter]::ToUInt32($Bytes, $entry + 12)
        if ($size -ne $height -or $sizes -contains $size -or $length -lt 24 -or
            $offset -ne $last -or ([long]$offset + $length) -gt $Bytes.Length) { throw "Invalid ICO frame: $Name" }
        $sizes += $size
        $last = [long]$offset + $length
        $memory = [IO.MemoryStream]::new($Bytes, [int]$offset, [int]$length, $false)
        try {
            $frame = [Drawing.Bitmap]::new($memory)
            try {
                if ($frame.Width -ne $size -or $frame.Height -ne $size -or
                    $frame.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid) {
                    throw "Wrong PNG dimensions or encoding: $Name"
                }
            } finally { $frame.Dispose() }
        } finally { $memory.Dispose() }
    }
    if ($last -ne $Bytes.Length -or (Compare-Object @(16,24,32,48,64,128,256) @($sizes | Sort-Object))) {
        throw "Unexpected ICO frames: $Name"
    }
}

function Test-AssociationLifecycle([string]$Folder) {
    $registry = [Microsoft.Win32.Registry]::CurrentUser
    foreach ($key in @('Software\Unbox\Capabilities','Software\UnzipProxy\Capabilities','Software\Classes\Unbox.zip')) {
        $existing = $registry.OpenSubKey($key)
        if ($existing) {
            $existing.Dispose()
            throw 'Association testing requires an isolated Windows user with no existing Unbox registration.'
        }
    }
    $extension = $registry.CreateSubKey('Software\Classes\.lz4\OpenWithProgids')
    $sentinel = 'UnboxDistributionTest.' + [Guid]::NewGuid().ToString('N')
    $extension.SetValue($sentinel, 'other application')
    $launcher = Join-Path $Folder '安装与卸载.cmd'
    try {
        & $launcher /install
        if ($LASTEXITCODE -ne 0) { throw 'Silent install failed' }
        $register = Join-Path $Folder '注册关联.ps1'
        if ((& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $register -Status 6>&1 | Out-String) -notmatch '完整登记') {
            throw 'Registration did not report complete'
        }
        $moved = Join-Path (Split-Path -Parent $Folder) '移动路径 (修复)'
        Move-Item -LiteralPath $Folder -Destination $moved
        $launcher = Join-Path $moved '安装与卸载.cmd'
        & $launcher /install
        if ($LASTEXITCODE -ne 0) { throw 'Repair after moving failed' }
        $register = Join-Path $moved '注册关联.ps1'
        if ((& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $register -Status 6>&1 | Out-String) -notmatch '完整登记') {
            throw 'Moved paths were not repaired'
        }
        foreach ($attempt in 1, 2) {
            & $launcher /uninstall
            if ($LASTEXITCODE -ne 0) { throw "Uninstall attempt $attempt failed" }
        }
        if ($extension.GetValue($sentinel) -ne 'other application' -or
            $extension.GetValueNames() -contains 'Unbox.lz4' -or
            -not (Test-Path -LiteralPath (Join-Path $moved 'Unbox.exe') -PathType Leaf)) {
            throw 'Uninstall changed unrelated registration, retained Unbox, or removed program files'
        }
    } finally {
        if (Test-Path -LiteralPath $launcher) { & $launcher /uninstall }
        $extension.DeleteValue($sentinel, $false)
        $extension.Dispose()
    }
}

$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Unbox-QA\distribution-' + [Guid]::NewGuid().ToString('N'))
try {
    $formatEntry = $archive.GetEntry('assets/archive-formats.json')
    if (-not $formatEntry) { throw 'Missing format catalog' }
    $reader = [IO.StreamReader]::new($formatEntry.Open(), [Text.Encoding]::UTF8)
    try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $formats = $json | ConvertFrom-Json
    $registered = @($formats.formats | Where-Object associate)
    if ($formats.schemaVersion -ne 1 -or $registered.Count -ne 57 -or
        @($registered.suffix | Sort-Object -Unique).Count -ne 57) { throw 'Invalid packaged associations' }
    $keys = @( (@('origin') + @($formats.formats | ForEach-Object iconKey)) | Sort-Object -Unique )
    $expected = @(
        'Unbox.exe','UnzipWorker.exe','7z.dll','安装与卸载.cmd','注册关联.ps1','取消关联.ps1',
        'README.md','使用说明.txt','LICENSE','assets/archive-formats.json','assets/licenses/7zip-zs/THIRD-PARTY.txt'
    ) + @($keys | ForEach-Object { "icons/$_.ico" })
    $actual = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    if ($actual.Count -ne $expected.Count -or (Compare-Object $expected $actual -CaseSensitive)) {
        throw 'ZIP entries differ from the portable distribution allowlist'
    }
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $package = Join-Path $testRoot '便携测试 (中文目录)'
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $package)
    $archive.Dispose()
    $archive = $null
    if ((Get-FileHash -LiteralPath (Join-Path $package '7z.dll') -Algorithm SHA256).Hash -ne $fixed) {
        throw 'Packaged DLL has the wrong fingerprint'
    }
    $catalogFile = Join-Path $package 'assets\archive-formats.json'
    $catalogHash = (Get-FileHash -LiteralPath $catalogFile -Algorithm SHA256).Hash
    $expectedCatalog = Join-Path $repo 'assets\archive-formats.json'
    if ((Test-Path -LiteralPath $expectedCatalog) -and
        (Get-FileHash -LiteralPath $expectedCatalog -Algorithm SHA256).Hash -ne $catalogHash) {
        throw 'Packaged catalog differs from source'
    }
    foreach ($key in $keys) {
        Test-IcoFrameTable ([IO.File]::ReadAllBytes((Join-Path $package "icons\$key.ico"))) $key
    }
    $exe = Join-Path $package 'Unbox.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($version.FileVersion -ne '1.4.0.0' -or $version.ProductVersion -ne '1.4.0') {
        throw "Unexpected Unbox.exe version: $($version.FileVersion) / $($version.ProductVersion)"
    }
    $exeText = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($exe))
    if (-not $exeText.Contains('Unbox.Finished.wav') -or -not $exeText.Contains('Unbox.ArchiveFormats.json') -or
        -not $exeText.Contains([Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($catalogFile)))) {
        throw 'Single-file WPF build lacks the expected embedded resources'
    }
    $brand = Join-Path $repo 'src\UnzipProxy\obj\UnboxBrand.ico'
    if (Test-Path -LiteralPath $brand) {
        $ico = [IO.File]::ReadAllBytes($brand)
        Test-IcoFrameTable $ico 'UnboxBrand'
        $count = [BitConverter]::ToUInt16($ico, 4)
        for ($i=0; $i -lt $count; ++$i) {
            $entry = 6 + 16 * $i
            $length = [BitConverter]::ToUInt32($ico, $entry + 8)
            $offset = [BitConverter]::ToUInt32($ico, $entry + 12)
            if (-not $exeText.Contains([Text.Encoding]::GetEncoding(28591).GetString($ico, $offset, $length))) {
                throw 'Single-file WPF build lacks a generated brand icon frame'
            }
        }
    }

    $fixture = Join-Path $testRoot 'fixture.zip'
    $zip = [IO.Compression.ZipFile]::Open($fixture, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('fixture.txt').Open())
        try { $writer.Write('Unbox distribution check') } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $staging = Join-Path $testRoot 'staging'
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $frames = & (Join-Path $package 'UnzipWorker.exe') --archive $fixture --staging $staging --manifest (Join-Path $testRoot 'manifest.json') |
        ForEach-Object { $_ | ConvertFrom-Json }
    if ($LASTEXITCODE -ne 0 -or ($frames | Where-Object type -eq init).catalog_sha256 -ne $catalogHash -or
        ($frames | Where-Object type -eq finished).status -ne 'ok' -or
        [IO.File]::ReadAllText((Join-Path $staging 'fixture.txt')) -ne 'Unbox distribution check') {
        throw 'Packaged Worker/DLL failed a smoke test or used a different catalog'
    }
    if ($VerifyAssociations) { Test-AssociationLifecycle $package }
    Write-Host "[OK] Portable ZIP verified ($($expected.Count) files, $($registered.Count) association candidates)"
} finally {
    if ($archive) { $archive.Dispose() }
    if ((Test-Path -LiteralPath $testRoot) -and
        [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($testRoot)) -eq
            [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'Unbox-QA'))) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
