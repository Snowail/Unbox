# sandbox_test_helper.ps1: 适用于 Windows Sandbox 干净环境的一键测试套件
$ErrorActionPreference = "Continue"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   Windows Sandbox · Unbox v1.1.0 干净环境验收" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$baseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$unboxDir = Join-Path $baseDir "Unbox-v1.1.0-win-x64"

if (-not (Test-Path $unboxDir)) {
    if (Test-Path (Join-Path $baseDir "..\dist\Unbox-v1.1.0-win-x64")) {
        $unboxDir = Join-Path $baseDir "..\dist\Unbox-v1.1.0-win-x64"
    } elseif (Test-Path (Join-Path $baseDir "Unbox.exe")) {
        $unboxDir = $baseDir
    } else {
        Write-Error "找不到 Unbox 目录！"
        return
    }
}

# 1. 检查 / 安装 .NET 10 桌面运行库
Write-Host "`n>>> [步骤 1/4] 检查 .NET 10 桌面运行时..." -ForegroundColor Yellow
$runtimes = & dotnet --list-runtimes 2>$null
$hasNet10 = $false
if ($runtimes) {
    foreach ($line in $runtimes) {
        if ($line -like "*Microsoft.WindowsDesktop.App 10.*") {
            $hasNet10 = $true
            break
        }
    }
}

if (-not $hasNet10) {
    Write-Host "沙盒内尚未检测到 .NET 10 桌面运行时，正在从微软官方下载并静默安装..." -ForegroundColor Cyan
    $runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe"
    $installerPath = "$env:TEMP\windowsdesktop-runtime-10.0.exe"
    try {
        Invoke-WebRequest -Uri $runtimeUrl -OutFile $installerPath -UseBasicParsing
        Start-Process -FilePath $installerPath -ArgumentList "/install /quiet /norestart" -Wait
        Write-Host "[OK] .NET 10 桌面运行时安装完成！" -ForegroundColor Green
    } catch {
        Write-Warning "自动下载安装失败，请手动在浏览器中安装: https://dotnet.microsoft.com/download/dotnet/10.0"
    }
} else {
    Write-Host "[OK] 检测到系统已就绪 .NET 10 桌面运行时" -ForegroundColor Green
}

# 2. 自动生成各场景测试压缩包
Write-Host "`n>>> [步骤 2/4] 生成测试压缩包样本..." -ForegroundColor Yellow
$testFolder = Join-Path $env:USERPROFILE "Desktop\Unbox_Test_Samples"
if (-not (Test-Path $testFolder)) { New-Item -ItemType Directory -Path $testFolder -Force | Out-Null }

$zipSample = Join-Path $testFolder "文档样本.zip"
$tmpSrc = Join-Path $env:TEMP "sample_docs"
if (Test-Path $tmpSrc) { Remove-Item $tmpSrc -Recurse -Force }
New-Item -ItemType Directory -Path $tmpSrc -Force | Out-Null
Set-Content -Path (Join-Path $tmpSrc "报告.txt") -Value "这是 Unbox 解压出的普通文档样本内容。"
Compress-Archive -Path "$tmpSrc\*" -DestinationPath $zipSample -Force
Write-Host "  已生成: $zipSample" -ForegroundColor Gray

# 3. 执行注册关联
Write-Host "`n>>> [步骤 3/4] 执行一键文件关联注册 (注册关联.ps1)..." -ForegroundColor Yellow
$regScript = Join-Path $unboxDir "注册关联.ps1"
if (Test-Path $regScript) {
    & powershell.exe -ExecutionPolicy Bypass -File $regScript
}

Write-Host "`n>>> [步骤 4/4] 验证与体验建议：" -ForegroundColor Yellow
Write-Host "1. 双击桌面 【Unbox_Test_Samples】 文件夹中的 【文档样本.zip】；" -ForegroundColor White
Write-Host "2. 观察 Win11 原生 Fluent 窗口动画、速度面积图及 100% 自动退出体验；" -ForegroundColor White
Write-Host "3. 测试完成后，可运行 【取消关联.ps1】 验证一键纯净卸载注册表与关联。" -ForegroundColor White
Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "  沙盒环境配置就绪！请尽情测试！" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
