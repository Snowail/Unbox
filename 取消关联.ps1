# 取消关联.ps1: 彻底清理 Unbox 的关联与注册表项
[CmdletBinding()]
param()

$ErrorActionPreference = "SilentlyContinue"

Write-Host "=========================================="
Write-Host "   Unbox · Windows 11 双击自动解压 取消关联"
Write-Host "=========================================="

$appIds = @("Unbox", "unbox", "UnzipProxy")

$hkcu = [Microsoft.Win32.Registry]::CurrentUser
$classesKey = $hkcu.OpenSubKey("Software\Classes", $true)
if ($classesKey) {
    $subKeys = $classesKey.GetSubKeyNames()
    foreach ($sk in $subKeys) {
        foreach ($aid in $appIds) {
            if ($sk.StartsWith("$aid.")) {
                $classesKey.DeleteSubKeyTree($sk, $false)
                Write-Host "  已移除 ProgID: $sk"
            }
        }
        # 清理 OpenWithProgids
        if ($sk.StartsWith(".")) {
            $openWith = $classesKey.OpenSubKey("$sk\OpenWithProgids", $true)
            if ($openWith) {
                $names = $openWith.GetValueNames()
                foreach ($n in $names) {
                    foreach ($aid in $appIds) {
                        if ($n.StartsWith("$aid.")) {
                            $openWith.DeleteValue($n, $false)
                        }
                    }
                }
                $openWith.Close()
            }
        }
    }
    $classesKey.Close()
}

# 清理 RegisteredApplications
$regAppsKey = $hkcu.OpenSubKey("Software\RegisteredApplications", $true)
if ($regAppsKey) {
    foreach ($aid in $appIds) {
        $regAppsKey.DeleteValue($aid, $false)
    }
    $regAppsKey.Close()
    Write-Host "  已从 RegisteredApplications 移除"
}

# 清理 Software\Unbox 及旧项
$swKey = $hkcu.OpenSubKey("Software", $true)
if ($swKey) {
    foreach ($aid in $appIds) {
        $swKey.DeleteSubKeyTree($aid, $false)
    }
    $swKey.Close()
    Write-Host "  已清理 Capabilities 注册信息"
}

# 刷新 Shell 关联与图标缓存
$csharp = @"
using System;
using System.Runtime.InteropServices;
public class ShellNotifierUnreg {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
"@
if (-not ([System.Management.Automation.PSTypeName]'ShellNotifierUnreg').Type) {
    Add-Type -TypeDefinition $csharp
}
[ShellNotifierUnreg]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "=========================================="
Write-Host "[OK] 全部文件关联与注册信息已成功清除！"
Write-Host "=========================================="
