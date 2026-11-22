[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Unregister-UnboxAssociations {
    $hkcu = [Microsoft.Win32.Registry]::CurrentUser
    $classes = $hkcu.OpenSubKey('Software\Classes', $true)
    $registered = $hkcu.OpenSubKey('Software\RegisteredApplications', $true)
    $catalog = Join-Path $PSScriptRoot 'assets\archive-formats.json'
    $known = '.zip .zipx .7z .rar .r00 .tar .ova .gz .gzip .tgz .tpz .bz2 .bzip2 .tbz2 .tbz .xz .txz .z .taz .zst .tzst .lzma .lzma86 .lzh .lha .cab .arj .wim .swm .esd .001 .deb .rpm .cpio .xar .xip .dmg .squashfs .vmdk .vdi .qcow .qcow2 .fat .ntfs .br .brotli .tbr .lz4 .tlz4 .lz5 .tlz5 .liz .tliz .lz .tlz .zstd .tzstd'.Split()
    if (Test-Path -LiteralPath $catalog -PathType Leaf) {
        try {
            $data = Get-Content -LiteralPath $catalog -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($data.schemaVersion -eq 1) {
                $known += @($data.formats | Where-Object { $_.associate -eq $true -and $_.suffix -cmatch '^\.[a-z0-9]+$' } |
                    ForEach-Object suffix)
            }
        } catch { Write-Warning '无法读取关联清单，将从已登记值及历史后缀清理。' }
    }

    $failures = [Collections.Generic.List[string]]::new()
    try {
        foreach ($id in @('Unbox', 'UnzipProxy')) {
            $capPath = "Software\$id\Capabilities"
            $cap = $hkcu.OpenSubKey($capPath, $true)
            $associations = if ($cap) { $cap.OpenSubKey('FileAssociations', $true) }
            try {
                $extensions = @($known)
                if ($associations) { $extensions += $associations.GetValueNames() }
                foreach ($ext in ($extensions | Sort-Object -Unique)) {
                    if ($ext -notmatch '^\.[a-z0-9]+$') { continue }
                    $progId = "$id$ext"
                    try {
                        $ownedMapping = $associations -and $associations.GetValue($ext) -eq $progId
                        $openWith = if ($classes) { $classes.OpenSubKey("$ext\OpenWithProgids", $true) }
                        $prog = if ($classes) { $classes.OpenSubKey("$progId\shell\open") }
                        $hasCandidate = $openWith -and $openWith.GetValueNames() -contains $progId
                        $ownedProg = $false
                        try {
                            if ($prog) {
                                $command = $prog.OpenSubKey('command')
                                try {
                                    $ownedProg = $command -and $command.GetValue('') -match '^"[^"]*\\(?:Unbox|UnzipProxy)\.exe"\s+"%1"$'
                                } finally { if ($command) { $command.Dispose() } }
                            }
                            if ($hasCandidate -and ($ownedMapping -or $ownedProg -or $known -contains $ext)) {
                                $openWith.DeleteValue($progId, $false)
                            }
                        } finally {
                            if ($openWith) { $openWith.Dispose() }
                            if ($prog) { $prog.Dispose() }
                        }
                        if ($classes -and ($ownedMapping -or $ownedProg)) {
                            $classes.DeleteSubKeyTree($progId, $false)
                        }
                        if ($ownedMapping) { $associations.DeleteValue($ext, $false) }
                    } catch { $failures.Add("$progId - $($_.Exception.Message)") }
                }
                if ($registered -and $registered.GetValue($id) -eq $capPath) {
                    $registered.DeleteValue($id, $false)
                }
                if ($cap -and $cap.GetValue('ApplicationName') -in @('Unbox', 'UnzipProxy', $id)) {
                    foreach ($name in @('ApplicationName','ApplicationDescription','ApplicationIcon')) {
                        $cap.DeleteValue($name, $false)
                    }
                }
                if ($associations -and $associations.ValueCount -eq 0 -and $associations.SubKeyCount -eq 0) {
                    $associations.Dispose()
                    $associations = $null
                    $cap.DeleteSubKey('FileAssociations', $false)
                }
                if ($cap -and $cap.ValueCount -eq 0 -and $cap.SubKeyCount -eq 0) {
                    $cap.Dispose()
                    $cap = $null
                    $hkcu.DeleteSubKey($capPath, $false)
                }
            } catch { $failures.Add("$capPath - $($_.Exception.Message)") }
            finally {
                if ($associations) { $associations.Dispose() }
                if ($cap) { $cap.Dispose() }
            }
        }
    } finally {
        if ($classes) { $classes.Dispose() }
        if ($registered) { $registered.Dispose() }
    }
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
}

try {
    Unregister-UnboxAssociations
    if (-not ('ShellNotifierUnreg' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShellNotifierUnreg {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
}
"@
    }
    [ShellNotifierUnreg]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    Write-Host '[OK] 已清理当前用户的 Unbox 文件关联。'
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
