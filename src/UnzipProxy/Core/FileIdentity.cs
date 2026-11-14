using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace UnzipProxy.Core
{
    /// <summary>
    /// 源卷对象身份：卷序列号 + 128 位文件 ID + 长度 + 修改时间。
    /// Worker 在真正读取分卷时捕获该快照，Proxy 在回收前重新读取并比对，
    /// 杜绝“同名文件被替换后误回收”这一类路径猜测式缺陷。
    ///
    /// 128 位文件 ID 采用“原始 16 字节”的规范表示（<see cref="FileId"/>），
    /// 传输时序列化为 32 位十六进制字符串，按 Identifier[0..15] 的顺序拼接，
    /// 与 Worker 端 FILE_ID_128.Identifier 的字节序完全一致，避免高低 64 位定义相反。
    /// </summary>
    public sealed class VolumeIdentity
    {
        public string Path { get; set; } = "";
        public ulong VolumeSerial { get; set; }
        public ulong FileIdHigh { get; set; }
        public ulong FileIdLow { get; set; }
        /// <summary>128 位文件 ID 的原始 16 字节（Identifier[0..15]），身份比对的唯一依据。</summary>
        public byte[] FileId { get; set; } = Array.Empty<byte>();
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
                        var fileId = new byte[16];
                        Marshal.Copy(IntPtr.Add(buffer, 8), fileId, 0, 16);
                        identity.FileId = fileId;
                        identity.FileIdHigh = ReadUInt64LittleEndian(fileId, 0);
                        identity.FileIdLow = ReadUInt64LittleEndian(fileId, 8);
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

        /// <summary>逐项比对：卷、原始 16 字节文件 ID、长度与修改时间必须全部一致。</summary>
        public bool Matches(VolumeIdentity current)
        {
            if (!Valid || !current.Valid) return false;
            if (FileId.Length != 16 || current.FileId.Length != 16) return false;
            return VolumeSerial == current.VolumeSerial
                   && FileId.AsSpan().SequenceEqual(current.FileId)
                   && Size == current.Size
                   && LastWriteTime == current.LastWriteTime;
        }

        public string Describe()
        {
            if (!Valid) return "无法读取对象身份";
            string fileIdHex = FileId.Length == 16 ? Convert.ToHexString(FileId).ToLowerInvariant() : "??";
            return $"卷 {VolumeSerial:X} / 文件 ID {fileIdHex} / {Size} 字节";
        }

        private static ulong ReadUInt64LittleEndian(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | bytes[offset + i];
            return value;
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
