# tests/Run-AllTests.ps1: 统一测试入口
# 调用 tests/Tester 的“生产完成路径”测试套件（与 WPF 使用同一套 ExtractionCompleter）。
$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$workerExe = Join-Path $rootDir "bin\UnzipWorker.exe"
$dll7z = Join-Path $rootDir "bin\7z.dll"

# 1. 定位 .NET SDK（优先使用用户级 .NET 10 安装）
$dotnetExe = "dotnet"
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    $dotnetExe = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} elseif (Test-Path (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")) {
    $dotnetExe = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
}

# 2. 缺 Worker 时先构建（需要 MSVC x64 工具链）
if (-not (Test-Path $workerExe) -or -not (Test-Path (Join-Path $rootDir "bin\PathSanitizerTests.exe"))) {
    Write-Host ">>> 未找到 UnzipWorker.exe，先执行原生构建..." -ForegroundColor Yellow
    & (Join-Path $rootDir "scripts\build_worker.ps1")
    if ($LASTEXITCODE -ne 0) { throw "UnzipWorker.exe 构建失败" }
}

# 3. 缺 7z.dll 时从 assets 补齐
if (-not (Test-Path $dll7z)) {
    $assetDll = Join-Path $rootDir "assets\7z.dll"
    if (Test-Path $assetDll) {
        Copy-Item -Path $assetDll -Destination $dll7z -Force
        Write-Host ">>> 已从 assets 复制 7z.dll" -ForegroundColor Yellow
    }
}

# 原生句柄相对创建与中间联接点边界回归。
& (Join-Path $rootDir "bin\PathSanitizerTests.exe")
if ($LASTEXITCODE -ne 0) { throw "原生边界测试失败" }

# 4. 运行生产路径测试套件
Write-Host ">>> 运行 tests/Tester（生产完成路径测试套件）..." -ForegroundColor Cyan
& $dotnetExe run --project (Join-Path $rootDir "tests\Tester\Tester.csproj") -c Release
if ($LASTEXITCODE -ne 0) {
    throw "测试未通过 (exit $LASTEXITCODE)"
}
Write-Host "[OK] 全部测试通过" -ForegroundColor Green
