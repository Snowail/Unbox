[CmdletBinding()]
param([switch]$NoSettings, [switch]$Status)

$ErrorActionPreference = 'Stop'
$appId = 'Unbox'
$regRelativePath = 'Software\Unbox\Capabilities'

function Get-UnboxLayout([string]$Root) {
    $bin = $Root
    if (-not (Test-Path -LiteralPath (Join-Path $bin 'Unbox.exe') -PathType Leaf)) {
        $bin = Join-Path $Root 'bin'
    }
    $application = Join-Path $bin 'Unbox.exe'
    if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw "找不到主程序：$application" }
    return @{
        Application = [IO.Path]::GetFullPath($application)
        Icons = Join-Path $bin 'icons'
        Catalog = Join-Path $Root 'assets\archive-formats.json'
    }
}

function Read-AssociationCatalog([string]$Path) {
    $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.schemaVersion -ne 1 -or -not $data.formats) { throw '关联清单版本无效或内容为空' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $data.formats) {
        if ($item.suffix -cnotmatch '^\.[a-z0-9]+(?:\.[a-z0-9]+)*$' -or -not $seen.Add($item.suffix) -or
            $item.associate -isnot [bool] -or ($item.associate -and
                ($item.suffix -cnotmatch '^\.[a-z0-9]+$' -or $item.iconKey -cnotmatch '^[a-z0-9]+$' -or
                 [string]::IsNullOrWhiteSpace($item.displayName)))) {
            throw "无效或重复的关联清单条目：$($item.suffix)"
        }
        if ($item.associate) { $item }
    }
}

function Get-UnboxStatus($Layout, $Entries) {
    $hkcu = [Microsoft.Win32.Registry]::CurrentUser
    $registered = $hkcu.OpenSubKey('Software\RegisteredApplications')
    $cap = $hkcu.OpenSubKey("$regRelativePath\FileAssociations")
    try {
        $registeredAs = if ($registered) { $registered.GetValue($appId) }
        if (-not $registeredAs -and -not $cap -and -not ($hkcu.OpenSubKey('Software\Classes\Unbox.zip'))) {
            return '未登记'
        }
        if ($registeredAs -ne $regRelativePath -or -not $cap -or $cap.ValueCount -ne $Entries.Count) {
            return '部分登记或旧目录'
        }
        foreach ($entry in $Entries) {
            $ext = $entry.suffix
            $progId = "Unbox$ext"
            $openWith = $hkcu.OpenSubKey("Software\Classes\$ext\OpenWithProgids")
            $command = $hkcu.OpenSubKey("Software\Classes\$progId\shell\open\command")
            $icon = $hkcu.OpenSubKey("Software\Classes\$progId\DefaultIcon")
            $expectedIcon = Join-Path $Layout.Icons ($entry.iconKey + '.ico')
            try {
                if ($cap.GetValue($ext) -ne $progId -or -not $command -or -not $icon -or -not $openWith -or
                    $command.GetValue('') -ne ('"{0}" "%1"' -f $Layout.Application) -or
                    $icon.GetValue('') -ne ('"{0}",0' -f $expectedIcon) -or
                    $openWith.GetValueNames() -notcontains $progId -or
                    $openWith.GetValueKind($progId) -ne [Microsoft.Win32.RegistryValueKind]::None) {
                    return '部分登记或旧目录'
                }
            } finally {
                if ($command) { $command.Dispose() }
                if ($icon) { $icon.Dispose() }
                if ($openWith) { $openWith.Dispose() }
            }
        }
        return "完整登记（$($Entries.Count) 项）"
    } finally {
        if ($registered) { $registered.Dispose() }
        if ($cap) { $cap.Dispose() }
    }
}

function Register-UnboxAssociations($Layout, $Entries, [switch]$NoSettings) {
    $proxyExe = $Layout.Application
    $iconsDir = $Layout.Icons
    $assocMap = [ordered]@{}
    foreach ($entry in $Entries) {
        $file = $entry.iconKey + '.ico'
        if (-not (Test-Path -LiteralPath (Join-Path $iconsDir $file) -PathType Leaf)) {
            throw "关联 $($entry.suffix) 缺少图标 $file"
        }
        $assocMap[$entry.suffix] = @{ Name = $entry.displayName; Icon = $file }
    }
Write-Host "正在通过 .NET 原生注册表引擎写入关联项..."

# 1. 打开/创建基础注册表项
$hkcu = [Microsoft.Win32.Registry]::CurrentUser
$classesKey = $hkcu.CreateSubKey("Software\Classes")
$appCapKey = $hkcu.CreateSubKey($regRelativePath)
$fileAssocKey = $appCapKey.CreateSubKey("FileAssociations")

# 写入 Capabilities 核心信息
$appCapKey.SetValue("ApplicationName", "Unbox")
$appCapKey.SetValue("ApplicationDescription", "双击自动解压代理")
$appCapKey.SetValue("ApplicationIcon", "`"$proxyExe`",0")

# 写入 RegisteredApplications
$regAppsKey = $hkcu.CreateSubKey("Software\RegisteredApplications")
$regAppsKey.SetValue($appId, $regRelativePath)
$regAppsKey.Close()

$count = 0
foreach ($ext in $assocMap.Keys) {
    $info = $assocMap[$ext]
    $cleanExt = $ext.TrimStart('.')
    $progId = "$appId.$cleanExt"

    $iconPath = Join-Path $iconsDir $info.Icon

    # 1. 创建 ProgID (包含 FriendlyTypeName，解决 Win11 默认应用面板不识别问题)
    $progIdKey = $classesKey.CreateSubKey($progId)
    $progIdKey.SetValue("", $info.Name)
    $progIdKey.SetValue("FriendlyTypeName", $info.Name)

    $iconKey = $progIdKey.CreateSubKey("DefaultIcon")
    $iconKey.SetValue("", "`"$iconPath`",0")
    $iconKey.Close()

    $cmdKey = $progIdKey.CreateSubKey("shell\open\command")
    $cmdKey.SetValue("", "`"$proxyExe`" `"%1`"")
    $cmdKey.Close()

    $openKey = $progIdKey.CreateSubKey("shell\open")
    $openKey.SetValue("FriendlyAppName", "Unbox")
    $openKey.Close()

    $progIdKey.Close()

    # 2. 登记到 HKCU\Software\Classes\.<ext>\OpenWithProgids
    $extKey = $classesKey.CreateSubKey($ext)
    $openWithKey = $extKey.CreateSubKey("OpenWithProgids")
    $openWithKey.SetValue($progId, [byte[]]@(), [Microsoft.Win32.RegistryValueKind]::None)
    $openWithKey.Close()
    $extKey.Close()

    # 3. 登记到 Capabilities\FileAssociations (单次创建，多次写入，绝不覆盖抹杀！)
    $fileAssocKey.SetValue($ext, $progId)
    $count++
}

$fileAssocKey.Close()
$appCapKey.Close()
$classesKey.Close()

Write-Host "  已成功登记 $count 个文件格式关联！"

# 2. 广播 SHChangeNotify 刷新 Shell 关联与图标缓存
Write-Host "正在通知 Windows Shell 刷新关联与图标..."
$csharp = @"
using System;
using System.Runtime.InteropServices;
public class ShellNotifierAssoc {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
"@
if (-not ([System.Management.Automation.PSTypeName]'ShellNotifierAssoc').Type) {
    Add-Type -TypeDefinition $csharp
}
# SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
[ShellNotifierAssoc]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "=========================================="
Write-Host "[OK] 全部 $count 个文件关联已成功登记到当前用户！"
if ($NoSettings) { return }
Write-Host "正在打开 Windows 设置以选择默认应用…"
Write-Host "=========================================="

# 3. 唤起 Windows 默认应用深链接
try {
    Start-Process "ms-settings:defaultapps?registeredAppUser=$appId"
} catch {
    Start-Process "ms-settings:defaultapps"
}

}
try {
    $layout = Get-UnboxLayout $PSScriptRoot
    $entries = @(Read-AssociationCatalog $layout.Catalog)
    if ($entries.Count -ne 57) { throw "预期 57 个候选关联，实际为 $($entries.Count)" }
    if ($Status) {
        Write-Host ("[系统状态] " + (Get-UnboxStatus $layout $entries))
    } else {
        Register-UnboxAssociations $layout $entries -NoSettings:$NoSettings
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}