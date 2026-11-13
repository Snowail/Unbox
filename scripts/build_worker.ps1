# Build UnzipWorker.exe using MSVC with dynamic environment detection
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$BinDir = Join-Path $RootDir "bin"
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

Write-Host "[BUILD] Compiling UnzipWorker.exe..." -ForegroundColor Cyan

$cmd = @"
`"$vcvars`" && cl.exe /nologo /O2 /utf-8 /std:c++20 /EHsc /DUNICODE /D_UNICODE /I`"$RootDir\src\UnzipWorker`" `"$RootDir\src\UnzipWorker\main.cpp`" /Fe`"$BinDir\UnzipWorker.exe`" /link ole32.lib oleaut32.lib bcrypt.lib advapi32.lib shell32.lib
"@

cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "[OK] UnzipWorker.exe compiled successfully: $BinDir\UnzipWorker.exe" -ForegroundColor Green
