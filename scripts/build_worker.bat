@echo off
setlocal enabledelayedexpansion

if defined VCVARS64_PATH if exist "%VCVARS64_PATH%" (
    call "%VCVARS64_PATH%"
    goto :compile
)

set VSWHERE="%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist %VSWHERE% set VSWHERE="%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist %VSWHERE% (
    for /f "usebackq tokens=*" %%i in (`%VSWHERE% -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
        set VS_DIR=%%i
    )
    if exist "!VS_DIR!\VC\Auxiliary\Build\vcvars64.bat" (
        call "!VS_DIR!\VC\Auxiliary\Build\vcvars64.bat"
        goto :compile
    )
)

where cl.exe >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] MSVC cl.exe not found!
    exit /b 1
)

:compile
set ROOT_DIR=%~dp0..
if not exist "%ROOT_DIR%\bin" mkdir "%ROOT_DIR%\bin"

echo [BUILD] Compiling UnzipWorker.exe...
cl.exe /nologo /O2 /std:c++20 /EHsc /DUNICODE /D_UNICODE ^
    /I"%ROOT_DIR%\src\UnzipWorker" ^
    "%ROOT_DIR%\src\UnzipWorker\main.cpp" ^
    /Fe"%ROOT_DIR%\bin\UnzipWorker.exe" ^
    /link ole32.lib oleaut32.lib bcrypt.lib advapi32.lib shell32.lib

if %errorlevel% neq 0 (
    echo [ERROR] Build failed!
    exit /b %errorlevel%
)

echo [OK] UnzipWorker.exe built successfully!
