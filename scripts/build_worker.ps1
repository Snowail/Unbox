# Build UnzipWorker.exe using MSVC with dynamic environment detection
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$BinDir = Join-Path $RootDir "bin"
$GeneratedDir = Join-Path $BinDir "generated"
if (-not (Test-Path $BinDir)) {
    New-Item -ItemType Directory -Path $BinDir | Out-Null
}

$vcvars = $null
if ($env:VCVARS64_PATH -and (Test-Path $env:VCVARS64_PATH)) {
    $vcvars = $env:VCVARS64_PATH
} else {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        $vswhere = "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    }
    if (Test-Path $vswhere) {
        $vsDir = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vsDir -and (Test-Path "$vsDir\VC\Auxiliary\Build\vcvars64.bat")) {
            $vcvars = "$vsDir\VC\Auxiliary\Build\vcvars64.bat"
        }
    }
}

if (-not $vcvars) {
    # Check if cl is already in PATH
    if (-not (Get-Command "cl.exe" -ErrorAction SilentlyContinue)) {
        Write-Error "MSVC environment not found. Please install VS Build Tools or specify VCVARS64_PATH."
        exit 1
    }
}

& (Join-Path $PSScriptRoot "Generate-FormatTable.ps1") -Catalog (Join-Path $RootDir "assets\archive-formats.json") -Output (Join-Path $GeneratedDir "ArchiveFormatTable.g.h")

Write-Host "[BUILD] Compiling UnzipWorker.exe..." -ForegroundColor Cyan

$setup = if ($vcvars) { ('call "{0}" && ' -f $vcvars) } else { "" }
$cmd = @"
${setup}cl.exe /nologo /O2 /utf-8 /std:c++20 /EHsc /DUNICODE /D_UNICODE /I`"$GeneratedDir`" `"$RootDir\src\UnzipWorker\main.cpp`" /Fo`"$BinDir\UnzipWorker.obj`" /Fe`"$BinDir\UnzipWorker.exe`" /link ole32.lib oleaut32.lib bcrypt.lib advapi32.lib shell32.lib
"@

cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# 原生安全边界回归测试与 Worker 使用同一 MSVC 工具链。
$testCmd = @"
${setup}cl.exe /nologo /O2 /utf-8 /std:c++20 /EHsc /DUNICODE /D_UNICODE /I`"$GeneratedDir`" `"$RootDir\tests\PathSanitizerTests.cpp`" /Fo`"$BinDir\PathSanitizerTests.obj`" /Fe`"$BinDir\PathSanitizerTests.exe`"
"@
cmd.exe /c $testCmd
if ($LASTEXITCODE -ne 0) { throw "PathSanitizerTests 编译失败" }

Write-Host "[OK] UnzipWorker.exe compiled successfully: $BinDir\UnzipWorker.exe" -ForegroundColor Green

$assetDll = Join-Path $RootDir 'assets\7z.dll'
$runtimeDll = Join-Path $BinDir '7z.dll'
$expectedDll = 'D480E5F7ACF03183DC8CD9FE341A06D34E768B3C6606C0DB4AEC2A7D0EB5098E'
if ((Get-FileHash -LiteralPath $assetDll -Algorithm SHA256).Hash -ne $expectedDll) {
    throw 'The repository 7z.dll does not match the pinned ZS release.'
}
Copy-Item -LiteralPath $assetDll -Destination $runtimeDll -Force
if ((Get-FileHash -LiteralPath $runtimeDll -Algorithm SHA256).Hash -ne $expectedDll) {
    throw 'The Worker 7z.dll does not match the pinned ZS release.'
}
