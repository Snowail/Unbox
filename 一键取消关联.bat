@echo off
chcp 65001 >nul
title Unbox · 一键取消文件关联
cd /d "%~dp0"

echo ========================================================
echo   Unbox · Windows 11 双击自动解压 正在取消关联...
echo ========================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\取消关联.ps1"

echo.
echo 按任意键退出...
pause >nul
