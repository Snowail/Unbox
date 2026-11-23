# scripts/package_release.ps1: 自动化构建便携绿色发布包 (Framework-Dependent .NET 10 LTS)
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "1.4.0",
    [switch]$SkipTests
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
$sumsFile = Join-Path $distDir "SHA256SUMS.txt"
$lockFile = Join-Path $distDir ".package-release.lock"
$publishTemp = Join-Path $distDir (".publish_" + [guid]::NewGuid().ToString("N"))
$candidateDir = Join-Path $distDir (".candidate_" + [guid]::NewGuid().ToString("N"))

# 删除构建缓存前，确认目标均为仓库 dist 下的直接产物。
if ((Test-Path -LiteralPath $distDir) -and
    ((Get-Item -LiteralPath $distDir -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "dist 目录不能为重解析点：$distDir"
}
foreach ($target in @($pkgDir, $zipFile, $sumsFile, $lockFile, $publishTemp, $candidateDir)) {
    $resolved = [System.IO.Path]::GetFullPath($target)
    if ([System.IO.Path]::GetDirectoryName($resolved) -ne [System.IO.Path]::GetFullPath($distDir)) {
        throw "构建路径不在预期 dist 目录中：$resolved"
    }
    if ((Test-Path -LiteralPath $resolved) -and
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "构建目标不能为重解析点：$resolved"
    }
}
[xml]$project = Get-Content -LiteralPath "$rootDir\src\UnzipProxy\UnzipProxy.csproj" -Raw
if ($project.Project.PropertyGroup.Version -ne $Version) {
    throw "发布版本与项目 Version 不一致：$Version"
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   制作 Unbox 便携绿色发布包 (v$Version)" -ForegroundColor Cyan
Write-Host "   架构: win-x64 (.NET 10 LTS 框架依赖版)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 确保目录纯净
if (Test-Path $pkgDir) {
    Remove-Item -LiteralPath $pkgDir -Recurse -Force
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

if (Test-Path $publishTemp) { Remove-Item -LiteralPath $publishTemp -Recurse -Force }
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
Remove-Item -LiteralPath $publishTemp -Recurse -Force

# 5. 严格验证必备组件完整性并复制至发布目录（杜绝任何关键文件缺失）
Write-Host ">>> [步骤 4/5] 严密核验发布物完整性并复制组件..." -ForegroundColor Yellow

$requiredCoreFiles = @(
    @{ Path = Join-Path $binDir "Unbox.exe";        Name = "Unbox.exe (WPF UI)" },
    @{ Path = Join-Path $binDir "UnzipWorker.exe";  Name = "UnzipWorker.exe (C++20 Engine)" },
    @{ Path = Join-Path $binDir "7z.dll";           Name = "7z.dll (7-Zip Library)" },
    @{ Path = Join-Path $rootDir "README.md";       Name = "README.md (说明文档)" },
    @{ Path = Join-Path $rootDir "LICENSE";         Name = "LICENSE (开源协议与署名)" },
    @{ Path = Join-Path $rootDir "安装与卸载.cmd";    Name = "安装与卸载.cmd (图形化管理菜单)" },
    @{ Path = Join-Path $rootDir "注册关联.ps1";    Name = "注册关联.ps1 (关联脚本)" },
    @{ Path = Join-Path $rootDir "取消关联.ps1";    Name = "取消关联.ps1 (卸载脚本)" }
)

foreach ($item in $requiredCoreFiles) {
    if (-not (Test-Path $item.Path)) {
        throw "发布阻断：必需组件缺失 - $($item.Name) ($($item.Path))"
    }
    Copy-Item -Path $item.Path -Destination $pkgDir -Force
    Write-Host "  [OK] $($item.Name)" -ForegroundColor Green
}

$catalogPath = Join-Path $rootDir "assets\archive-formats.json"
$licensePath = Join-Path $rootDir "assets\licenses\7zip-zs\THIRD-PARTY.txt"
$licenseDir = Join-Path $pkgDir "assets\licenses\7zip-zs"
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $pkgDir "assets\archive-formats.json")
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $licenseDir "THIRD-PARTY.txt")
$formats = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$iconKeys = @( (@("origin") + @($formats.formats | ForEach-Object iconKey)) | Sort-Object -Unique )
$targetIconsDir = Join-Path $pkgDir "icons"
New-Item -ItemType Directory -Path $targetIconsDir -Force | Out-Null
foreach ($key in $iconKeys) {
    if ($key -cnotmatch '^[a-z0-9]+$') { throw "无效图标键：$key" }
    Copy-Item -LiteralPath (Join-Path $binDir "icons\$key.ico") -Destination $targetIconsDir
}

# Use absolute repository asset links in the portable README.
$readmePath = Join-Path $pkgDir "README.md"
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
$publicAssets = 'https://raw.githubusercontent.com/Snowail/Unbox/main/'
$readme = $readme.Replace('](appicon/package-box.png)', ('](' + $publicAssets + 'appicon/package-box.png)'))
$readme = $readme.Replace('src="assets/banner.png"', ('src="' + $publicAssets + 'assets/banner.png"'))
Set-Content -LiteralPath $readmePath -Value $readme -Encoding UTF8

# 自动化回归测试（若未跳过，测试日志输出至 dist 外部目录，绝不打包入发布包）
if (-not $SkipTests) {
    Write-Host ">>> 执行自动化回归测试门禁..." -ForegroundColor Yellow
    $buildLogsDir = Join-Path $distDir ".build_logs"
    if (-not (Test-Path $buildLogsDir)) { New-Item -ItemType Directory -Path $buildLogsDir -Force | Out-Null }
    $testLog = Join-Path $buildLogsDir "test_run.log"
    Push-Location $rootDir
    try {
        & "$rootDir\tests\Run-AllTests.ps1" *>&1 | Tee-Object -FilePath $testLog
        if ($LASTEXITCODE -ne 0) { throw "发布回归测试未完全通过，中止打包！" }
    } finally { Pop-Location }
    Write-Host "  [OK] 自动化回归测试全量通过" -ForegroundColor Green
}

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
2. 双击【安装与卸载.cmd】，选择【1 安装或修复关联】；
3. 系统为 57 种格式登记打开方式与专用图标；
4. 在 Windows 默认应用中将所需格式选为 Unbox，随后双击关联的压缩包即可解压。

【格式与解压上限】
内核为固定的 7-Zip 26.05 ZS v1.5.7 R1。支持新压缩流与显式 TAR 别名，以及五种 ZS 7z 加密方法的读取。
普通压缩流只解外层。未知大小的流若触及解压上限，可为当前任务输入有限的新上限后重试。

【卸载方法】
双击【安装与卸载.cmd】，选择【2 卸载关联】以移除 Unbox 的文件关联。
该入口只为当前 PowerShell 进程指定执行策略。

开源协议：MIT License / 7-Zip LGPL / 图标原设计由 smalllikeart 授权
========================================================================
"@
Set-Content -Path (Join-Path $pkgDir "使用说明.txt") -Value $readmeText -Encoding utf8

# 7. 核验发布包内容
Write-Host ">>> [步骤 5/5] 打包便携绿色压缩包..." -ForegroundColor Yellow

$disallowedPatterns = @(
    "*test*", "*Test*", "*.cpp", "*.obj", "*.pdb",
    "*plan*", "*review*", "*report*", "*HANDOFF*",
    "*.log", "*.tmp", "构建信息.txt"
)
$leakedFiles = Get-ChildItem -Path $pkgDir -Recurse | Where-Object {
    $item = $_
    foreach ($pat in $disallowedPatterns) {
        if ($item.Name -like $pat) { return $true }
    }
    return $false
}
if ($leakedFiles) {
    throw "发布阻断：检测到非发布文件混入发布包目录：$($leakedFiles.FullName -join ', ')"
}
Write-Host "  [OK] 发布包目录纯净审计通过（无测试、报告或历史记录）" -ForegroundColor Green

# 8. 在独立目录生成并验收候选附件，再切换正式文件名。
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$candidateZip = Join-Path $candidateDir "$pkgName.zip"
$candidateSums = Join-Path $candidateDir "SHA256SUMS.txt"
$previousDir = Join-Path $candidateDir "previous"
$keepCandidate = $false
New-Item -ItemType Directory -Path $candidateDir | Out-Null
try {
    $zip = [IO.Compression.ZipFile]::Open($candidateZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $pkgDir -Recurse -File) {
            $name = $file.FullName.Substring($pkgDir.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    & (Join-Path $rootDir 'tests\Test-Distribution.ps1') -ZipPath $candidateZip

    $zipSize = (Get-Item -LiteralPath $candidateZip).Length
    $zipSizeMB = [math]::Round($zipSize / 1MB, 2)
    $zipHash = (Get-FileHash -LiteralPath $candidateZip -Algorithm SHA256).Hash
    Set-Content -LiteralPath $candidateSums -Value "$zipHash  $pkgName.zip" -Encoding ASCII

    $lock = [IO.FileStream]::new($lockFile, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    try {
        New-Item -ItemType Directory -Path $previousDir | Out-Null
        $attachments = @(
            @{ Candidate = $candidateZip; Final = $zipFile; Previous = (Join-Path $previousDir "$pkgName.zip") },
            @{ Candidate = $candidateSums; Final = $sumsFile; Previous = (Join-Path $previousDir "SHA256SUMS.txt") }
        )
        foreach ($attachment in $attachments) {
            if ([IO.File]::Exists($attachment.Final)) {
                [IO.File]::Copy($attachment.Final, $attachment.Previous)
            }
        }

        # A stopped promotion can leave the two final names out of sync.
        # Keep the complete previous pair until both replacements or rollback finish.
        $keepCandidate = $true
        $promoted = @()
        try {
            foreach ($attachment in $attachments) {
                if ([IO.File]::Exists($attachment.Previous)) {
                    [IO.File]::Replace($attachment.Candidate, $attachment.Final, [NullString]::Value)
                } else {
                    [IO.File]::Move($attachment.Candidate, $attachment.Final)
                }
                $promoted += $attachment
            }
        } catch {
            try {
                foreach ($attachment in $promoted) {
                    if ([IO.File]::Exists($attachment.Previous)) {
                        [IO.File]::Replace($attachment.Previous, $attachment.Final, [NullString]::Value)
                    } else {
                        [IO.File]::Move($attachment.Final, $attachment.Candidate)
                    }
                }
            } catch {
                throw "附件切换后恢复失败。上一套附件的备份位于 $previousDir。$($_.Exception.Message)"
            }
            $keepCandidate = $false
            throw
        }
        $keepCandidate = $false
    } finally { $lock.Dispose() }
} finally {
    if (-not $keepCandidate) { Remove-Item -LiteralPath $candidateDir -Recurse -Force }
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[OK] 发布便携包制作成功！" -ForegroundColor Green
Write-Host "发布包路径: $zipFile" -ForegroundColor Green
Write-Host "发布包体积: $zipSizeMB MB ($zipSize 字节)" -ForegroundColor Green
Write-Host "SHA-256   : $zipHash" -ForegroundColor Green
Write-Host "发布包内容清单:" -ForegroundColor Yellow
Get-ChildItem -Path $pkgDir | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "==========================================" -ForegroundColor Cyan
