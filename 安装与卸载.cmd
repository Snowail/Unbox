@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
set "PSEXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if /i "%~1"=="1" goto install_quiet
if /i "%~1"=="install" goto install_quiet
if /i "%~1"=="/install" goto install_quiet
if /i "%~1"=="-install" goto install_quiet
if /i "%~1"=="2" goto uninstall_quiet
if /i "%~1"=="uninstall" goto uninstall_quiet
if /i "%~1"=="/uninstall" goto uninstall_quiet
if /i "%~1"=="-uninstall" goto uninstall_quiet
if not "%~1"=="" exit /b 1

:menu
cls
echo Unbox 安装与卸载
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1" -Status
echo.
echo   [1] 安装或修复关联
echo   [2] 卸载关联
echo   [0] 退出
setlocal EnableDelayedExpansion
set "choice="
set /p "choice=请选择 [1/2/0]："
if "!choice!"=="1" (endlocal & goto install)
if "!choice!"=="2" (endlocal & goto uninstall)
if "!choice!"=="0" (endlocal & exit /b 0)
if /i "!choice!"=="q" (endlocal & exit /b 0)
if not defined choice (endlocal & exit /b 0)
endlocal
goto menu

:install
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1"
goto result

:uninstall
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"

:result
if errorlevel 1 (echo 操作失败，退出代码 %errorlevel%。) else (echo 操作完成。)
pause >nul
goto menu

:install_quiet
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0注册关联.ps1" -NoSettings
exit /b %errorlevel%

:uninstall_quiet
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0取消关联.ps1"
exit /b %errorlevel%
