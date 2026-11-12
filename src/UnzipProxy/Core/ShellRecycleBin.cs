using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UnzipProxy.Core
{
    public static class ShellRecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        // 查找属于同一组的分卷文件
        public static List<string> FindVolumeFamily(string sourcePath)
        {
            var family = new List<string>();
            if (!File.Exists(sourcePath)) return family;

            string? dir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();

            string fileName = Path.GetFileName(sourcePath);

            // 1. .7z.001 / .001 系列
            Match m001 = Regex.Match(fileName, @"^(.*?)\.(\d{3})$", RegexOptions.IgnoreCase);
            if (m001.Success)
            {
                string prefix = m001.Groups[1].Value;
                foreach (string file in Directory.GetFiles(dir, prefix + ".*"))
                {
                    if (Regex.IsMatch(Path.GetFileName(file), @"^" + Regex.Escape(prefix) + @"\.\d{3}$", RegexOptions.IgnoreCase))
                    {
                        family.Add(file);
                    }
                }
                if (family.Count > 0) return family;
            }

            // 2. .part01.rar / .part1.rar 系列
            Match mPart = Regex.Match(fileName, @"^(.*?)\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
            if (mPart.Success)
            {
                string prefix = mPart.Groups[1].Value;
                foreach (string file in Directory.GetFiles(dir, prefix + ".part*.rar"))
                {
                    family.Add(file);
                }
                if (family.Count > 0) return family;
            }

            // 3. .z01, .z02 ... .zip 系列
            Match mZipPart = Regex.Match(fileName, @"^(.*?)\.(z\d{2}|zip)$", RegexOptions.IgnoreCase);
            if (mZipPart.Success)
            {
                string prefix = mZipPart.Groups[1].Value;
                foreach (string file in Directory.GetFiles(dir, prefix + ".*"))
                {
                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(ext, @"^\.z\d{2}$", RegexOptions.IgnoreCase))
                    {
                        family.Add(file);
                    }
                }
                if (family.Count > 0) return family;
            }

            // 普通单文件
            family.Add(sourcePath);
            return family;
        }

        // 检查路径是否可能支持回收站（网络路径 \\ 和某些虚拟盘通常不支持）
        public static bool IsRecycleBinSupported(string path)
        {
            try
            {
                if (path.StartsWith(@"\\")) return false; // UNC 路径不支持回收站
                string root = Path.GetPathRoot(path) ?? "";
                if (string.IsNullOrEmpty(root)) return true;

                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network) return false;
                if (drive.DriveType == DriveType.CDRom) return false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        // 安全移入回收站
        public static (bool success, string message) RecycleItems(IEnumerable<string> filePaths)
        {
            var existingFiles = new List<string>();
            foreach (var f in filePaths)
            {
                if (File.Exists(f))
                {
                    if (!IsRecycleBinSupported(f))
                    {
                        return (false, $"路径不支持回收站（网络驱动器或只读介质）：{Path.GetFileName(f)}，源文件已保留");
                    }
                    existingFiles.Add(Path.GetFullPath(f));
                }
            }

            if (existingFiles.Count == 0)
            {
                return (true, "无需回收");
            }

            // SHFileOperation 要求以双 null 结尾的多字符串缓冲
            string buffer = string.Join("\0", existingFiles) + "\0\0";

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = buffer,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                fAnyOperationsAborted = false
            };

            int ret = SHFileOperation(ref op);
            if (ret == 0 && !op.fAnyOperationsAborted)
            {
                return (true, $"已成功移入回收站 ({existingFiles.Count} 个文件)");
            }
            else
            {
                return (false, $"移入回收站未完成 (错误码: 0x{ret:X})，源文件已保留");
            }
        }
    }
}
