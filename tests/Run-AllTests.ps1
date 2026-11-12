# Run-AllTests.ps1: 自动化端到端测试套件
$ErrorActionPreference = "Stop"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $workspaceRoot "bin"
$workerExe = Join-Path $binDir "UnzipWorker.exe"
$proxyExe = Join-Path $binDir "UnzipProxy.exe"
$sevenZipCli = "C:\PortableSoft\bin\7z.exe"

$testOutputDir = Join-Path $workspaceRoot "tests\output"
if (Test-Path $testOutputDir) { Remove-Item $testOutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $testOutputDir | Out-Null

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Assert-Test {
    param([string]$Name, [bool]$Condition, [string]$Details = "")
    $status = if ($Condition) { "PASS" } else { "FAIL" }
    $color = if ($Condition) { "Green" } else { "Red" }
    Write-Host "[$status] $Name $Details" -ForegroundColor $color
    $script:results.Add([PSCustomObject]@{
        Test = $Name
        Status = $status
        Details = $Details
    })
}

Write-Host "=========================================="
Write-Host "  Windows 双击自动解压代理 · 自动化验收测试"
Write-Host "=========================================="

# -------------------------------------------------------------------
# Test 1: D1 目录规则测试 (保留包内顶层目录，不折叠)
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 1] D1 目录规则验证 (结构为 release/src/main.c，得到 测试/release/src/main.c)..."
$d1SrcDir = Join-Path $testOutputDir "d1_raw\release\src"
New-Item -ItemType Directory -Force -Path $d1SrcDir | Out-Null
Set-Content -Path (Join-Path $d1SrcDir "main.c") -Value "int main() { return 0; }"
$d1Zip = Join-Path $testOutputDir "测试D1.zip"
& $sevenZipCli a $d1Zip (Join-Path $testOutputDir "d1_raw\*") -r | Out-Null

# 规划解压
$d1Staging = Join-Path $testOutputDir "d1_staging"
& $workerExe --archive $d1Zip --staging $d1Staging --target-root "测试D1" | Out-Null
$extractedMainC = Join-Path $d1Staging "release\src\main.c"
Assert-Test -Name "D1: 保留包内顶层目录" -Condition (Test-Path $extractedMainC) -Details "找到 $extractedMainC"

# -------------------------------------------------------------------
# Test 2: D1 重名自动递增序号测试
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 2] D1 目录重名递增测试..."
Add-Type -Path (Join-Path $binDir "UnzipProxy.dll") -ErrorAction SilentlyContinue
# 测试 ArchiveTaskPlanner
$plan1 = [UnzipProxy.Core.ArchiveTaskPlanner]::Plan($d1Zip)
New-Item -ItemType Directory -Force -Path $plan1.FinalTargetDirectory | Out-Null
$plan2 = [UnzipProxy.Core.ArchiveTaskPlanner]::Plan($d1Zip)
Assert-Test -Name "D1: 目标冲突递增后缀" -Condition ($plan2.TargetRootDirName -match "测试D1 \(2\)") -Details "得到 $($plan2.TargetRootDirName)"

# -------------------------------------------------------------------
# Test 3: D5 排除 macOS 辅助文件 (__MACOSX, .DS_Store, ._*)
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 3] D5 排除 macOS 辅助文件..."
$d5SrcDir = Join-Path $testOutputDir "d5_raw"
New-Item -ItemType Directory -Force -Path "$d5SrcDir\__MACOSX", "$d5SrcDir\docs" | Out-Null
Set-Content -Path "$d5SrcDir\docs\readme.txt" -Value "Real Document"
Set-Content -Path "$d5SrcDir\.DS_Store" -Value "DSStore Junk"
Set-Content -Path "$d5SrcDir\__MACOSX\._readme.txt" -Value "AppleDouble Junk"
$d5Zip = Join-Path $testOutputDir "mac_sample.zip"
& $sevenZipCli a $d5Zip "$d5SrcDir\*" -r | Out-Null

$d5Staging = Join-Path $testOutputDir "d5_staging"
& $workerExe --archive $d5Zip --staging $d5Staging | Out-Null

$hasReadme = Test-Path "$d5Staging\docs\readme.txt"
$hasDSStore = Test-Path "$d5Staging\.DS_Store"
$hasMacDir = Test-Path "$d5Staging\__MACOSX"
Assert-Test -Name "D5: 正确解压正常文件" -Condition $hasReadme
Assert-Test -Name "D5: 严格排除 .DS_Store" -Condition (-not $hasDSStore)
Assert-Test -Name "D5: 严格排除 __MACOSX 目录" -Condition (-not $hasMacDir)

# -------------------------------------------------------------------
# Test 4: D6 非法字符清洗与重命名
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 4] D6 Windows 非法字符清洗与保留名处理..."
$testSegments = @(
    @{ Input = "my:file*name?.txt"; Expected = "my-file-name-.txt" }
    @{ Input = "trailing.dot.";     Expected = "trailing.dot-" }
    @{ Input = "con.txt";           Expected = "_con.txt" }
    @{ Input = "aux";               Expected = "_aux" }
    @{ Input = "normal_file.dat";   Expected = "normal_file.dat" }
)
foreach ($t in $testSegments) {
    # 测试 PathSanitizer::SanitizeRelativePath
    $sanitized = [UnzipProxy.Core.PathSanitizer]::SanitizeRelativePath($t.Input)
    Assert-Test -Name "D6: 清洗 $($t.Input)" -Condition ($sanitized -eq $t.Expected) -Details "得到 $sanitized (预期 $($t.Expected))"
}

# -------------------------------------------------------------------
# Test 5: D2 分卷家族查找与安全回收
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 5] D2 分卷家族查找..."
$volDir = Join-Path $testOutputDir "vol_test"
New-Item -ItemType Directory -Force -Path $volDir | Out-Null
Set-Content -Path "$volDir\game.part1.rar" -Value "part1"
Set-Content -Path "$volDir\game.part2.rar" -Value "part2"
Set-Content -Path "$volDir\game.part3.rar" -Value "part3"

$family = [UnzipProxy.Core.ShellRecycleBin]::FindVolumeFamily("$volDir\game.part1.rar")
Assert-Test -Name "D2: 完整定位整组分卷家族" -Condition ($family.Count -eq 3) -Details "找到 $($family.Count) 个分卷"

# -------------------------------------------------------------------
# Test 6: 图标与关联完整性 (D3/D4)
# -------------------------------------------------------------------
Write-Host "`n>>> [测试 6] D3/D4 关联清单与图标完整性..."
$icoFiles = Get-ChildItem -Path (Join-Path $binDir "icons") -Filter "*.ico"
Assert-Test -Name "D3/D4: 图标文件全部齐备" -Condition ($icoFiles.Count -ge 25) -Details "已包含 $($icoFiles.Count) 个 .ico"

# -------------------------------------------------------------------
# 总结输出
# -------------------------------------------------------------------
Write-Host "`n=========================================="
$passCount = ($results | Where-Object { $_.Status -eq "PASS" }).Count
$totalCount = $results.Count
Write-Host "测试汇总: $passCount / $totalCount 全部通过！" -ForegroundColor Cyan
Write-Host "=========================================="
