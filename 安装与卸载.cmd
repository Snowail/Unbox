@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Unbox - 安装与卸载管理

:: 锁定工作目录为当前脚本所在目录（防止右键管理员运行时路径被重设为 System32）
cd /d "%~dp0"

:: ------------------------------------------------------------
:: 命令行参数直接调用支持 (CLI 静默/批处理模式)
:: ------------------------------------------------------------
if "%~1"=="1" goto DO_INSTALL_CLI
if /i "%~1"=="/install" goto DO_INSTALL_CLI
if /i "%~1"=="-install" goto DO_INSTALL_CLI
if /i "%~1"=="install" goto DO_INSTALL_CLI

if "%~1"=="2" goto DO_UNINSTALL_CLI
if /i "%~1"=="/uninstall" goto DO_UNINSTALL_CLI
if /i "%~1"=="-uninstall" goto DO_UNINSTALL_CLI
if /i "%~1"=="uninstall" goto DO_UNINSTALL_CLI

:: ------------------------------------------------------------
:: 交互式菜单主循环
:: ------------------------------------------------------------
:MENU
cls
set "IS_INSTALLED=0"
set "REG_CMD="
set "LOCAL_EXE="

:: 1. 检测本地 Unbox 主程序路径（兼容根目录便携版与开发目录 bin\Unbox.exe）
if exist "%~dp0Unbox.exe" (
    set "LOCAL_EXE=%~dp0Unbox.exe"
) else if exist "%~dp0bin\Unbox.exe" (
    set "LOCAL_EXE=%~dp0bin\Unbox.exe"
) else if exist "%~dp0bin\unbox.exe" (
    set "LOCAL_EXE=%~dp0bin\unbox.exe"
) else if exist "%~dp0bin\UnzipProxy.exe" (
    set "LOCAL_EXE=%~dp0bin\UnzipProxy.exe"
) else if exist "%~dp0UnzipProxy.exe" (
    set "LOCAL_EXE=%~dp0UnzipProxy.exe"
)

:: 2. 检测当前用户是否已注册 Unbox 关联及关联目标路径
reg query "HKCU\Software\Classes\Unbox.zip" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    set "IS_INSTALLED=1"
    for /f "tokens=2,*" %%a in ('reg query "HKCU\Software\Classes\Unbox.zip\shell\open\command" /ve 2^>nul ^| findstr "REG_SZ"') do (
        set "REG_CMD=%%b"
    )
)

:: 3. 头部信息与当前状态呈现
echo ============================================================
echo         Unbox · 双击自动解压代理 ^| 安装与卸载管理
echo ============================================================
if "%IS_INSTALLED%"=="1" (
    echo [系统状态] 【已安装】44 种文件格式关联已登记生效
    if defined REG_CMD echo [注册目标] %REG_CMD%
) else (
    echo [系统状态] 【未安装】尚未在当前系统注册文件关联
)

if defined LOCAL_EXE (
    echo [本地程序] %LOCAL_EXE%
) else (
    echo [本地程序] [警告] 未检测到 Unbox.exe，请确保文件完整！
)

:: 路径一致性比对（已安装状态下检测是否移动过文件夹）
if "%IS_INSTALLED%"=="1" if defined LOCAL_EXE if defined REG_CMD (
    echo %REG_CMD% | findstr /i /c:"%LOCAL_EXE%" >nul 2>&1
    if errorlevel 1 (
        echo [路径提示] 检测到已注册路径与当前目录不一致，选择 [1] 可重定位
    ) else (
        echo [路径提示] 系统注册目标与本地程序路径完全一致
    )
)

echo ------------------------------------------------------------
echo 请选择要执行的操作:
echo.
if "%IS_INSTALLED%"=="1" goto MENU_INSTALLED
goto MENU_NOT_INSTALLED

:MENU_INSTALLED
echo   [1] 重新注册 / 修复文件关联 (更新至当前目录)
echo   [2] 彻底卸载 / 清理全部关联 【推荐】
goto MENU_CHOICE_PROMPT

:MENU_NOT_INSTALLED
echo   [1] 一键安装 / 注册 44 种文件格式关联 【推荐】
echo   [2] 清理残留 / 取消关联注册表项
goto MENU_CHOICE_PROMPT

:MENU_CHOICE_PROMPT
echo   [0] 退出
echo.
echo ============================================================
set "CHOICE="
set /p "CHOICE=请输入选项 [1/2/0] 并按回车: "

if "%CHOICE%"=="1" goto DO_INSTALL
if "%CHOICE%"=="2" goto DO_UNINSTALL
if "%CHOICE%"=="0" goto DO_EXIT
if /i "%CHOICE%"=="q" goto DO_EXIT

echo.
echo [提示] 无效的输入 "%CHOICE%"，请输入 1、2 或 0。
ping 127.0.0.1 -n 2 >nul
goto MENU

:: ------------------------------------------------------------
:: 执行安装 / 注册关联
:: ------------------------------------------------------------
:DO_INSTALL
cls
echo ============================================================
echo   正在执行：安装与注册文件关联...
echo ============================================================
echo.
if not exist "%~dp0注册关联.ps1" (
    echo [错误] 未在当前目录下找到 "注册关联.ps1"！
    echo 请确认脚本位于 Unbox 根目录下。
    echo.
    echo 按任意键返回主菜单...
    pause >nul
    goto MENU
)

:: 绕过 PowerShell 策略限制执行脚本
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1"

echo.
echo ============================================================
echo 操作已完成，按任意键返回主菜单刷新状态...
pause >nul
goto MENU

:: ------------------------------------------------------------
:: 执行卸载 / 取消关联
:: ------------------------------------------------------------
:DO_UNINSTALL
cls
echo ============================================================
echo   正在执行：卸载与清理文件关联...
echo ============================================================
echo.
if not exist "%~dp0取消关联.ps1" (
    echo [错误] 未在当前目录下找到 "取消关联.ps1"！
    echo 请确认脚本位于 Unbox 根目录下。
    echo.
    echo 按任意键返回主菜单...
    pause >nul
    goto MENU
)

:: 绕过 PowerShell 策略限制执行脚本
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"

echo.
echo ============================================================
echo 操作已完成，按任意键返回主菜单刷新状态...
pause >nul
goto MENU

:: ------------------------------------------------------------
:: 退出程序
:: ------------------------------------------------------------
:DO_EXIT
cls
echo 正在退出...
exit /b 0

:: ------------------------------------------------------------
:: 命令行静默调用入口
:: ------------------------------------------------------------
:DO_INSTALL_CLI
if not exist "%~dp0注册关联.ps1" (
    echo [错误] 未在当前目录下找到 "注册关联.ps1"！
    exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1"
exit /b %ERRORLEVEL%

:DO_UNINSTALL_CLI
if not exist "%~dp0取消关联.ps1" (
    echo [错误] 未在当前目录下找到 "取消关联.ps1"！
    exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"
exit /b %ERRORLEVEL%