@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "UNBOX_SETUP_ROOT=%~dp0"
"%SystemRoot%\System32\chcp.com" 65001 >nul

if "%~1"=="" goto menu
if "%~1"=="1" goto install
if /i "%~1"=="/install" goto install
if /i "%~1"=="-install" goto install
if /i "%~1"=="install" goto install
if "%~1"=="2" goto uninstall
if /i "%~1"=="/uninstall" goto uninstall
if /i "%~1"=="-uninstall" goto uninstall
if /i "%~1"=="uninstall" goto uninstall
exit /b 2

:menu
set "UNBOX_SETUP_ACTION=menu"
goto run

:install
set "UNBOX_SETUP_ACTION=install"
goto run

:uninstall
set "UNBOX_SETUP_ACTION=uninstall"

:run
rem Keep this file ASCII. PowerShell reads the UTF-8 BOM scripts and resolves their Chinese names.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$register = (-join [char[]](0x6CE8,0x518C,0x5173,0x8054)) + '.ps1'; $unregister = (-join [char[]](0x53D6,0x6D88,0x5173,0x8054)) + '.ps1'; $ErrorActionPreference = 'Stop'; $global:LASTEXITCODE = 0; switch ($env:UNBOX_SETUP_ACTION) { 'menu' { & (Join-Path $env:UNBOX_SETUP_ROOT $register) -Menu } 'install' { & (Join-Path $env:UNBOX_SETUP_ROOT $register) -NoSettings } 'uninstall' { & (Join-Path $env:UNBOX_SETUP_ROOT $unregister) } }; exit $LASTEXITCODE"
exit /b %errorlevel%
