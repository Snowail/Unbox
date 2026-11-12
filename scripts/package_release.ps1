# scripts/package_release.ps1: 自动化构建便携绿色发布包
[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$binDir = Join-Path $rootDir "bin"
$distDir = Join-Path $rootDir "dist"
$pkgName = "Unbox-v$Version-win-x64"
$pkgDir = Join-Path $distDir $pkgName
$zipFile = Join-Path $distDir "$pkgName.zip"

Write-Host "=========================================="
Write-Host "   制作 Unbox 便携绿色发布包 (v$Version)"
Write-Host "=========================================="

# 1. 确保目标目录纯净
if (Test-Path $pkgDir) {
    Remove-Item -Path $pkgDir -Recurse -Force
}
if (Test-Path $zipFile) {
    Remove-Item -Path $zipFile -Force
}
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

# 2. 拷贝核心程序与必要组件
Write-Host "正在复制核心程序与解码引擎..."
$filesToCopy = @(
    "Unbox.exe",
    "UnzipWorker.exe",
    "7z.dll",
    "Finished.mp3",
    "icon.png",
    "wpfgfx_cor3.dll",
    "PresentationNative_cor3.dll",
    "D3DCompiler_47_cor3.dll",
    "PenImc_cor3.dll",
    "vcruntime140_cor3.dll"
)

foreach ($f in $filesToCopy) {
    $srcPath = Join-Path $binDir $f
    if (Test-Path $srcPath) {
        Copy-Item -Path $srcPath -Destination $pkgDir -Force
    } else {
        Write-Warning "文件未找到: $srcPath"
    }
}

# 复制兼容别名 unbox.exe
$unboxExeSrc = Join-Path $binDir "unbox.exe"
if (Test-Path $unboxExeSrc) {
    Copy-Item -Path $unboxExeSrc -Destination (Join-Path $pkgDir "unbox.exe") -Force
} else {
    Copy-Item -Path (Join-Path $pkgDir "Unbox.exe") -Destination (Join-Path $pkgDir "unbox.exe") -Force
}

# 3. 复制图标资源目录
Write-Host "正在复制图标库..."
$iconsSrc = Join-Path $binDir "icons"
if (-not (Test-Path $iconsSrc)) {
    $iconsSrc = Join-Path $rootDir "icons"
}
Copy-Item -Path $iconsSrc -Destination (Join-Path $pkgDir "icons") -Recurse -Force

# 4. 复制一键关联/取消脚本
Write-Host "正在复制注册与清理脚本..."
Copy-Item -Path (Join-Path $rootDir "注册关联.ps1") -Destination $pkgDir -Force
Copy-Item -Path (Join-Path $rootDir "取消关联.ps1") -Destination $pkgDir -Force

# 5. 生成极简使用说明
$readmeContent = @"
========================================================================
   Unbox · Windows 11 双击自动解压代理 (便携绿色版 v$Version)
========================================================================

【软件简介】
Unbox 是专为 Windows 11 设计的极简无扰、双击自动解压工具。
界面深度高仿 Windows 11 原生文件复制对话框，具备 Fluent 现代设计美感，
底层搭载纯原生 C++20 解码核心与官方 7-Zip 引擎，性能强劲、内存占用极低。

【使用方法】
1. 首次使用请解压本文件夹到您希望放置的常驻目录（如 D:\Tools\Unbox 或 C:\Program Files\Unbox 等）；
2. 右键单击【注册关联.ps1】，选择【使用 PowerShell 运行】（或在当前目录打开终端执行 .\注册关联.ps1）；
3. 系统将自动为常见压缩包格式（.zip, .7z, .rar, .tar.gz 等 44 种格式）登记专用图标与双击解压代理；
4. 随后直接双击任意压缩包，即可自动开始流式解压！
5. 解压完成后窗口自动播放提示音并轻盈退出。

【快捷特性】
- 支持默认折叠与点击“详细信息”展开 Win11 原生速度堆积面积图与剩余指标；
- 底部支持一键勾选“解压完成后移入回收站”（仅在校验完全成功时移入，安全有保障）；
- 自动智能剥离复合后缀（如 .tar.gz 一步提取 TAR 内容并建立纯净包名文件夹）；
- 目标目录重名时自动递增后缀（测试、测试 (2)），绝不覆盖原有文件；
- 自动智能过滤 macOS 产生的垃圾辅助文件（__MACOSX、.DS_Store 等）；
- 智能编码还原，杜绝跨平台文件名乱码。

【卸载与清理】
若您不再需要使用本工具，只需右键单击【取消关联.ps1】并选择【使用 PowerShell 运行】，
即可彻底、干净地清理所有写入系统的关联和注册表项。

开源协议：MIT License / 7-Zip LGPL
========================================================================
"@
Set-Content -Path (Join-Path $pkgDir "使用说明.txt") -Value $readmeContent -Encoding utf8

# 6. 打包为 ZIP 压缩包
Write-Host "正在压缩生成便携包: $zipFile ..."
Compress-Archive -Path "$pkgDir\*" -DestinationPath $zipFile -Force

Write-Host "=========================================="
Write-Host "[OK] 便携绿色发布包制作完成！"
Write-Host "目录路径: $pkgDir"
Write-Host "ZIP 压缩包: $zipFile"
Write-Host "=========================================="
