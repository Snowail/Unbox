using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UnzipProxy.Core
{
    /// <summary>
    /// 源卷对象身份：卷序列号 + 128 位文件 ID + 长度 + 修改时间。
    /// Worker 在真正读取分卷时捕获该快照，Proxy 在回收前重新读取并比对，
    /// 杜绝“同名文件被替换后误回收”这一类路径猜测式缺陷。
    /// </summary>
    public sealed class VolumeIdentity
    {
        public string Path { get; set; } = "";
        public ulong VolumeSerial { get; set; }
        public ulong FileIdHigh { get; set; }
        public ulong FileIdLow { get; set; }
        public long Size { get; set; }
        public long LastWriteTime { get; set; }
        public bool Valid { get; set; }

        public static VolumeIdentity? Capture(string path, bool requireValid = true)
        {
            var identity = new VolumeIdentity { Path = path };
            IntPtr handle = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80 /* FILE_ATTRIBUTE_NORMAL */, IntPtr.Zero);
            if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
            {
                return requireValid ? null : identity;
            }

            try
            {
                IntPtr buffer = Marshal.AllocHGlobal(24);
                try
                {
                    if (GetFileInformationByHandleEx(handle, 18 /* FileIdInfo */, buffer, (uint)24))
                    {
                        identity.VolumeSerial = (ulong)Marshal.ReadInt64(buffer, 0);
                        identity.FileIdLow = (ulong)Marshal.ReadInt64(buffer, 8);
                        identity.FileIdHigh = (ulong)Marshal.ReadInt64(buffer, 16);
                        identity.Valid = true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                {
                    identity.Size = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
                    identity.LastWriteTime = ((long)info.ftLastWriteTimeHigh << 32) | info.ftLastWriteTimeLow;
                }
                else
                {
                    identity.Valid = false;
                }
            }
            finally
            {
                CloseHandle(handle);
            }

            return requireValid && !identity.Valid ? null : identity;
        }

        /// <summary>逐项比对：卷、文件 ID、长度与修改时间必须全部一致。</summary>
        public bool Matches(VolumeIdentity current)
        {
            if (!Valid || !current.Valid) return false;
            return VolumeSerial == current.VolumeSerial
                   && FileIdHigh == current.FileIdHigh
                   && FileIdLow == current.FileIdLow
                   && Size == current.Size
                   && LastWriteTime == current.LastWriteTime;
        }

        public string Describe()
        {
            return Valid
                ? $"卷 {VolumeSerial:X} / 文件 ID {FileIdHigh:X16}{FileIdLow:X16} / {Size} 字节"
                : "无法读取对象身份";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
                                                 IntPtr lpSecurityAttributes, uint dwCreationDisposition,
                                                 uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int fileInformationClass,
                                                                IntPtr lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
