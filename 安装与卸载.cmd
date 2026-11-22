@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
title Unbox - 安装与卸载管理

if "%~1"=="1" goto install_cli
if /i "%~1"=="/install" goto install_cli
if /i "%~1"=="-install" goto install_cli
if /i "%~1"=="install" goto install_cli
if "%~1"=="2" goto uninstall_cli
if /i "%~1"=="/uninstall" goto uninstall_cli
if /i "%~1"=="-uninstall" goto uninstall_cli
if /i "%~1"=="uninstall" goto uninstall_cli
if not "%~1"=="" exit /b 2

:menu
cls
echo Unbox - 安装与卸载管理
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1" -Status
if errorlevel 1 echo [系统状态] 无法读取本地程序或关联清单
echo.
echo   [1] 安装或修复关联
echo   [2] 卸载关联
echo   [0] 退出
choice /c 120 /n /m "请选择操作 [1/2/0] "
if errorlevel 3 exit /b 0
if errorlevel 2 goto uninstall
if errorlevel 1 goto install
exit /b 1

:install
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1"
goto result

:uninstall
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"
goto result

:result
if errorlevel 1 (echo 操作失败，请查看以上信息。) else echo 操作完成。
pause >nul
goto menu

:install_cli
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1" -NoSettings
exit /b %errorlevel%

:uninstall_cli
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"
exit /b %errorlevel%
