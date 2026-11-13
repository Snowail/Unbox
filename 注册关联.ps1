# 注册关联.ps1: 注册 Unbox 文件关联并登记到 Windows 11 默认应用平台
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir = $scriptDir
if (Test-Path (Join-Path $scriptDir "bin\Unbox.exe")) {
    $binDir = Join-Path $scriptDir "bin"
} elseif (Test-Path (Join-Path $scriptDir "bin\unbox.exe")) {
    $binDir = Join-Path $scriptDir "bin"
}

$proxyExe = Join-Path $binDir "Unbox.exe"
if (-not (Test-Path $proxyExe)) {
    $proxyExe = Join-Path $binDir "unbox.exe"
}
$iconsDir = Join-Path $binDir "icons"
if (-not (Test-Path $iconsDir)) {
    $iconsDir = Join-Path $scriptDir "icons"
}

if (-not (Test-Path $proxyExe)) {
    # 尝试兼容 UnzipProxy.exe
    $altExe = Join-Path $binDir "UnzipProxy.exe"
    if (Test-Path $altExe) {
        $proxyExe = $altExe
    } else {
        Write-Error "找不到主程序: $proxyExe，请先完成构建发布！"
        exit 1
    }
}

Write-Host "=========================================="
Write-Host "   Unbox · Windows 11 双击自动解压关联注册"
Write-Host "=========================================="
Write-Host "主程序路径: $proxyExe"
Write-Host "图标资源目录: $iconsDir"

# 定义支持关联的格式清单及其对应图标 (符合 D3/D4：排除 ISO, VHD, VHDX，排除办公文档/安装包/可执行文件)
$assocMap = [ordered]@{
    # 常用压缩包
    ".zip"      = @{ Name = "ZIP 归档文件";        Icon = "zip.ico" }
    ".zipx"     = @{ Name = "ZIPX 扩展归档文件";    Icon = "zip.ico" }
    ".7z"       = @{ Name = "7-Zip 压缩文件";      Icon = "7z.ico" }
    ".rar"      = @{ Name = "WinRAR 压缩文件";     Icon = "rar.ico" }
    ".r00"      = @{ Name = "WinRAR 分卷文件";     Icon = "rar.ico" }
    ".tar"      = @{ Name = "TAR 归档文件";        Icon = "tar.ico" }
    ".ova"      = @{ Name = "开放虚拟归档文件";    Icon = "tar.ico" }
    
    # 压缩流与复合别名
    ".gz"       = @{ Name = "GZip 压缩文件";       Icon = "gz.ico" }
    ".gzip"     = @{ Name = "GZip 压缩文件";       Icon = "gz.ico" }
    ".tgz"      = @{ Name = "GZip TAR 归档";      Icon = "gz.ico" }
    ".tpz"      = @{ Name = "GZip TAR 归档";      Icon = "gz.ico" }
    ".bz2"      = @{ Name = "BZip2 压缩文件";      Icon = "bz.ico" }
    ".bzip2"    = @{ Name = "BZip2 压缩文件";      Icon = "bz.ico" }
    ".tbz2"     = @{ Name = "BZip2 TAR 归档";     Icon = "bz.ico" }
    ".tbz"      = @{ Name = "BZip2 TAR 归档";     Icon = "bz.ico" }
    ".xz"       = @{ Name = "XZ 压缩文件";         Icon = "xz.ico" }
    ".txz"      = @{ Name = "XZ TAR 归档";        Icon = "xz.ico" }
    ".z"        = @{ Name = "UNIX Compress 文件";  Icon = "z.ico" }
    ".taz"      = @{ Name = "TAR Z 压缩归档";      Icon = "z.ico" }
    ".zst"      = @{ Name = "Zstandard 压缩文件";  Icon = "z.ico" }
    ".tzst"     = @{ Name = "Zstandard TAR 归档";  Icon = "z.ico" }
    ".lzma"     = @{ Name = "LZMA 压缩文件";       Icon = "lz.ico" }
    ".lzma86"   = @{ Name = "LZMA86 压缩文件";     Icon = "lz.ico" }
    ".lzh"      = @{ Name = "LHA/LZH 压缩文件";    Icon = "lzh.ico" }
    ".lha"      = @{ Name = "LHA 压缩文件";        Icon = "lzh.ico" }
    ".cab"      = @{ Name = "Windows Cabinet 文件"; Icon = "cab.ico" }
    ".arj"      = @{ Name = "ARJ 压缩归档";        Icon = "arj.ico" }
    ".wim"      = @{ Name = "Windows 映像文件";    Icon = "wim.ico" }
    ".swm"      = @{ Name = "分卷 Windows 映像";   Icon = "wim.ico" }
    ".esd"      = @{ Name = "ESD 部署映像文件";    Icon = "wim.ico" }
    
    # 分卷入口与发行包
    ".001"      = @{ Name = "切分/分卷压缩首卷";   Icon = "01.ico" }
    ".deb"      = @{ Name = "Debian 软件包";       Icon = "deb.ico" }
    ".rpm"      = @{ Name = "RedHat 软件包";       Icon = "rpm.ico" }
    ".cpio"     = @{ Name = "CPIO 归档文件";       Icon = "cp.ico" }
    ".xar"      = @{ Name = "XAR 归档文件";        Icon = "xa.ico" }
    ".xip"      = @{ Name = "XIP 安全归档文件";    Icon = "xa.ico" }
    
    # 非 Explorer 原生挂载的磁盘/虚拟机镜像
    ".dmg"      = @{ Name = "Apple 磁盘映像";      Icon = "dmg.ico" }
    ".squashfs" = @{ Name = "SquashFS 文件系统映像"; Icon = "sq.ico" }
    ".vmdk"     = @{ Name = "VMware 虚拟磁盘映像"; Icon = "vmdk.ico" }
    ".vdi"      = @{ Name = "VirtualBox 磁盘映像"; Icon = "vdi.ico" }
    ".qcow"     = @{ Name = "QEMU 磁盘映像";       Icon = "qcow.ico" }
    ".qcow2"    = @{ Name = "QEMU 磁盘映像 v2";    Icon = "qcow.ico" }
    ".fat"      = @{ Name = "FAT 分区映像";        Icon = "fat.ico" }
    ".ntfs"     = @{ Name = "NTFS 分区映像";       Icon = "ntfs.ico" }
}

$appId = "Unbox"
$regRelativePath = "Software\$appId\Capabilities"

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
    if (-not (Test-Path $iconPath)) {
        $iconPath = Join-Path $iconsDir "origin.ico"
    }

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
Write-Host "正在打开 Windows 11 设置页供您设为默认应用..."
Write-Host "=========================================="

# 3. 唤起 Windows 默认应用深链接
try {
    Start-Process "ms-settings:defaultapps?registeredAppUser=$appId"
} catch {
    Start-Process "ms-settings:defaultapps"
}
