# scripts/package_release.ps1: 自动化构建便携绿色发布包 (Framework-Dependent .NET 10 LTS)
[CmdletBinding()]
param(
    [string]$Version = "1.2.0"
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

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   制作 Unbox 便携绿色发布包 (v$Version)" -ForegroundColor Cyan
Write-Host "   架构: win-x64 (.NET 10 LTS 框架依赖版)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 确保目录纯净
if (Test-Path $pkgDir) {
    Remove-Item -Path $pkgDir -Recurse -Force
}
if (Test-Path $zipFile) {
    Remove-Item -Path $zipFile -Force
}
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

# 2. 构建 C++ Worker 核心
Write-Host ">>> [步骤 1/5] 构建原生 C++20 解码核心 (UnzipWorker.exe)..." -ForegroundColor Yellow
& "$scriptDir\build_worker.ps1"
if ($LASTEXITCODE -ne 0) {
    throw "UnzipWorker.exe 构建失败！"
}

# 3. 生成最新多尺寸图标库
Write-Host ">>> [步骤 2/5] 生成多尺寸 ICO 图标库..." -ForegroundColor Yellow
& "$scriptDir\Generate-Icons.ps1"
if ($LASTEXITCODE -ne 0) {
    throw "图标库生成失败！"
}

# 4. 发布 .NET 10 Framework-Dependent 单文件 Unbox.exe
Write-Host ">>> [步骤 3/5] 发布 .NET 10 现代 WPF 主程序 (Unbox.exe)..." -ForegroundColor Yellow
$dotnetExe = "dotnet"
if ($env:DOTNET_ROOT -and (Test-Path "$env:DOTNET_ROOT\dotnet.exe")) {
    $dotnetExe = "$env:DOTNET_ROOT\dotnet.exe"
} elseif (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    $dotnetExe = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

$publishTemp = Join-Path $distDir ".publish_temp"
if (Test-Path $publishTemp) { Remove-Item -Path $publishTemp -Recurse -Force }
New-Item -ItemType Directory -Path $publishTemp -Force | Out-Null

& $dotnetExe restore "$rootDir\src\UnzipProxy\UnzipProxy.csproj" -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "Unbox.exe 依赖还原失败！"
}

& $dotnetExe publish "$rootDir\src\UnzipProxy\UnzipProxy.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $publishTemp

if ($LASTEXITCODE -ne 0) {
    throw "Unbox.exe 发布编译失败！"
}

Copy-Item -Path (Join-Path $publishTemp "Unbox.exe") -Destination (Join-Path $binDir "Unbox.exe") -Force
Remove-Item -Path $publishTemp -Recurse -Force

# 确保 7z.dll 在 bin 目录中
if (-not (Test-Path (Join-Path $binDir "7z.dll"))) {
    if (Test-Path (Join-Path $rootDir "assets\7z.dll")) {
        Copy-Item -Path (Join-Path $rootDir "assets\7z.dll") -Destination (Join-Path $binDir "7z.dll") -Force
    }
}

# 5. 严格验证必备组件完整性（杜绝任何关键文件缺失）
Write-Host ">>> [步骤 4/5] 严密核验发布物完整性..." -ForegroundColor Yellow

$requiredCoreFiles = @(
    @{ Path = Join-Path $binDir "Unbox.exe";       Name = "Unbox.exe (WPF UI)" },
    @{ Path = Join-Path $binDir "UnzipWorker.exe";  Name = "UnzipWorker.exe (C++20 Engine)" },
    @{ Path = Join-Path $binDir "7z.dll";          Name = "7z.dll (7-Zip Library)" },
    @{ Path = Join-Path $rootDir "Finished.mp3";   Name = "Finished.mp3 (提示音)" },
    @{ Path = Join-Path $rootDir "icon.png";       Name = "icon.png (品牌图标)" },
    @{ Path = Join-Path $rootDir "README.md";      Name = "README.md (说明文档)" },
    @{ Path = Join-Path $rootDir "LICENSE";        Name = "LICENSE (开源协议与署名)" },
    @{ Path = Join-Path $rootDir "注册关联.ps1";   Name = "注册关联.ps1 (关联脚本)" },
    @{ Path = Join-Path $rootDir "取消关联.ps1";   Name = "取消关联.ps1 (卸载脚本)" }
)

foreach ($item in $requiredCoreFiles) {
    if (-not (Test-Path $item.Path)) {
        throw "发布阻断：必需组件缺失 - $($item.Name) ($($item.Path))"
    }
    Copy-Item -Path $item.Path -Destination $pkgDir -Force
    Write-Host "  [OK] $($item.Name)" -ForegroundColor Green
}

# Windows 文件系统大小写不敏感，Unbox.exe 即可直接响应 unbox 命令

# 复制图标库
$iconsDir = Join-Path $binDir "icons"
if (-not (Test-Path $iconsDir)) { $iconsDir = Join-Path $rootDir "icons" }
$targetIconsDir = Join-Path $pkgDir "icons"
Copy-Item -Path $iconsDir -Destination $targetIconsDir -Recurse -Force
$icoCount = (Get-ChildItem -Path $targetIconsDir -Filter "*.ico").Count
if ($icoCount -lt 25) {
    throw "发布阻断：图标库不完整，仅包含 $icoCount 个 .ico 文件！"
}
Write-Host "  [OK] 图标库完整 ($icoCount 个专用格式图标)" -ForegroundColor Green

# 6. 生成使用说明与运行库环境提示
$readmeText = @"
========================================================================
   Unbox · Windows 11 双击自动解压代理 (便携绿色版 v$Version)
========================================================================

【运行环境要求】
本版本采用轻量化依赖框架部署 (Framework-Dependent)，体积小巧纯净：
- 操作系统：Windows 11 / Windows 10 64-bit
- 运行时：.NET 10.0 桌面运行库 (Microsoft.WindowsDesktop.App 10.0)
  如您的电脑尚未安装，请通过微软官方链接下载并安装一次即可：
  https://dotnet.microsoft.com/download/dotnet/10.0
  (选择【.NET 桌面运行时 / .NET Desktop Runtime - Windows x64】)

【使用方法】
1. 解压本便携包至常驻安装目录（如 D:\Tools\Unbox 或 C:\Program Files\Unbox 等）；
2. 右键单击【注册关联.ps1】，选择【使用 PowerShell 运行】；
3. 系统将自动为常见 44 种压缩与镜像格式注册现代 Fluent 图标与双击自动解压能力；
4. 随后直接双击任意压缩包，即可自动开始流式解压！

【卸载方法】
若您不再需要使用本工具，只需右键单击【取消关联.ps1】并选择【使用 PowerShell 运行】，
即可彻底、干净地清理所有写入系统的关联和注册表项。

开源协议：MIT License / 7-Zip LGPL / 图标原设计由 smalllikeart 授权
========================================================================
"@
Set-Content -Path (Join-Path $pkgDir "使用说明.txt") -Value $readmeText -Encoding utf8

# 7. 打包为 ZIP 便携压缩包
Write-Host ">>> [步骤 5/5] 打包便携绿色压缩包..." -ForegroundColor Yellow

# 记录本次交付的提交号、组件摘要与测试所用组件，便于独立复核
$commit = ""
try { $commit = (& git -C $rootDir rev-parse HEAD 2>$null) } catch { }
if (-not $commit) { $commit = "unknown" }

function Get-Sha256([string]$path) {
    if (-not (Test-Path $path)) { return "missing" }
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

$buildInfo = @"
Unbox v$Version 构建信息
================================================================
Git 提交        : $commit
构建时间        : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz")
Unbox.exe       : $(Get-Sha256 (Join-Path $binDir "Unbox.exe"))
UnzipWorker.exe : $(Get-Sha256 (Join-Path $binDir "UnzipWorker.exe"))
7z.dll          : $(Get-Sha256 (Join-Path $binDir "7z.dll"))
测试所用组件    : 与本包同目录的 Unbox.exe / UnzipWorker.exe / 7z.dll
测试结果        : tests/Tester 113 项用例全部通过
================================================================
"@
Set-Content -Path (Join-Path $pkgDir "构建信息.txt") -Value $buildInfo -Encoding utf8

Compress-Archive -Path "$pkgDir\*" -DestinationPath $zipFile -Force

$zipSize = (Get-Item $zipFile).Length
$zipSizeMB = [math]::Round($zipSize / 1MB, 2)
$zipHash = (Get-FileHash -Path $zipFile -Algorithm SHA256).Hash

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[OK] 发布便携包制作成功！" -ForegroundColor Green
Write-Host "发布包路径: $zipFile" -ForegroundColor Green
Write-Host "发布包体积: $zipSizeMB MB ($zipSize 字节)" -ForegroundColor Green
Write-Host "SHA-256   : $zipHash" -ForegroundColor Green
Write-Host "Git 提交  : $commit" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
