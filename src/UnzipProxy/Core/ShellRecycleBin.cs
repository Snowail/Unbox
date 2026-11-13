using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

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

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        // 检查指定路径所在的驱动器是否真正支持回收站（排查 UNC 共享、网络驱动器及回收站禁用的情况）
        public static bool IsRecycleBinSupported(string path)
        {
            try
            {
                if (path.StartsWith(@"\\")) return false; // UNC 共享路径不支持回收站
                string root = Path.GetPathRoot(path) ?? "";
                if (string.IsNullOrEmpty(root)) return false;

                var drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    return false;
                }

                var rbInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                int hr = SHQueryRecycleBin(root, ref rbInfo);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        // 安全移入回收站：严格仅回收 Worker 报告的实际成功打开的分卷真实路径（杜绝正则盲猜与同名误杀）
        public static (bool success, string message) RecycleOpenedVolumes(IEnumerable<string> openedVolumes)
        {
            var targetFiles = new List<string>();
            foreach (var vol in openedVolumes)
            {
                if (string.IsNullOrWhiteSpace(vol)) continue;
                string fullPath = Path.GetFullPath(vol);
                if (!File.Exists(fullPath))
                {
                    return (false, $"分卷文件不存在或已被移除：{Path.GetFileName(vol)}，源文件已保留");
                }
                if (!IsRecycleBinSupported(fullPath))
                {
                    return (false, $"驱动器不支持回收站（可能为网络路径或未启用）：{Path.GetFileName(vol)}，源文件已保留");
                }
                targetFiles.Add(fullPath);
            }

            if (targetFiles.Count == 0)
            {
                return (true, "无需回收");
            }

            // 逐个执行回收，并在操作后校验文件是否已离开原位置且未造成意外损坏
            var recycled = new List<string>();
            foreach (var file in targetFiles)
            {
                // SHFileOperation 要求以双 null 结尾
                string buffer = file + "\0\0";
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = buffer,
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                    fAnyOperationsAborted = false
                };

                int ret = SHFileOperation(ref op);
                if (ret != 0 || op.fAnyOperationsAborted || File.Exists(file))
                {
                    return (false, $"分卷移入回收站失败：{Path.GetFileName(file)} (错误代码: 0x{ret:X})，后续源文件已停止回收");
                }
                recycled.Add(file);
            }

            return (true, $"已将源压缩包移入回收站 ({recycled.Count} 个分卷)");
        }
    }
}
