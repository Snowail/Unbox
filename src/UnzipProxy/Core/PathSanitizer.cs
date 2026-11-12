using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UnzipProxy.Core
{
    public static class PathSanitizer
    {
        private static readonly char[] IllegalChars = new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' };
        private static readonly string[] ReservedNames = new[] {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static bool IsMacAuxiliary(string relPath)
        {
            string norm = relPath.Replace('/', '\\');
            if (norm.StartsWith("__MACOSX\\", StringComparison.OrdinalIgnoreCase) ||
                norm.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string fileName = Path.GetFileName(norm);
            if (fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (fileName.StartsWith("._", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public static string SanitizeSegment(string seg)
        {
            if (string.IsNullOrEmpty(seg)) return seg;

            char[] chars = seg.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c < 32 || Array.IndexOf(IllegalChars, c) >= 0)
                {
                    chars[i] = '-';
                }
            }

            string res = new string(chars);
            // 处理尾部空格或点（全部替换为 '-'）
            int lastNonDotOrSpace = res.Length - 1;
            while (lastNonDotOrSpace >= 0 && (res[lastNonDotOrSpace] == ' ' || res[lastNonDotOrSpace] == '.'))
            {
                lastNonDotOrSpace--;
            }
            if (lastNonDotOrSpace < res.Length - 1)
            {
                res = res.Substring(0, lastNonDotOrSpace + 1) + new string('-', res.Length - 1 - lastNonDotOrSpace);
            }

            // 处理 Windows 设备保留名
            string baseName = Path.GetFileNameWithoutExtension(res);
            foreach (var r in ReservedNames)
            {
                if (baseName.Equals(r, StringComparison.OrdinalIgnoreCase))
                {
                    res = "_" + res;
                    break;
                }
            }

            return res;
        }

        public static string SanitizeRelativePath(string relPath)
        {
            string norm = relPath.Replace('/', '\\');
            string[] parts = norm.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "..") parts[i] = "__";
                parts[i] = SanitizeSegment(parts[i]);
            }
            return string.Join("\\", parts);
        }
    }
}
