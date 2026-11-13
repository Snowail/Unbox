using System;
using System.IO;

namespace UnzipProxy.Core
{
    /// <summary>
    /// 标准 IEEE 802.3 CRC32（反射多项式 0xEDB88320，初值与结果异或值均为 0xFFFFFFFF），
    /// 与 Worker 端 Crc32Hasher 的输出完全一致，用于交付清单的逐文件对账。
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
                }
                table[i] = crc;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
        {
            uint crc = seed ^ 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint ComputeFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            return ComputeStream(stream);
        }

        public static uint ComputeStream(Stream stream)
        {
            uint crc = 0xFFFFFFFFu;
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                }
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
