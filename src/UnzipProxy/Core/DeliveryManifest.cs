using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnzipProxy.Core
{
    /// <summary>交付清单中的单条记录（路径一律相对交付根目录）。</summary>
    public sealed class ManifestItem
    {
        public string Path { get; init; } = "";
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public uint Crc { get; init; }
    }

    /// <summary>Worker 产出的交付清单：期望的最终树逐项描述。</summary>
    public sealed class DeliveryManifest
    {
        public string DeliveryRootRelative { get; init; } = "";
        public int ItemCount { get; init; }
        public List<ManifestItem> Items { get; init; } = new();

        public static DeliveryManifest Load(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                throw new InvalidDataException("交付清单不存在");
            }
            return Parse(File.ReadAllText(manifestPath));
        }

        public static DeliveryManifest Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("交付清单格式错误");
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            {
                throw new InvalidDataException("交付清单版本不受支持");
            }
            string deliveryRoot = root.TryGetProperty("delivery_root", out var dr) ? (dr.GetString() ?? "") : "";
            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("交付清单缺少 items");
            }

            var manifest = new DeliveryManifest
            {
                DeliveryRootRelative = deliveryRoot,
                ItemCount = root.TryGetProperty("item_count", out var ic) ? ic.GetInt32() : -1
            };

            foreach (var element in itemsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("交付清单条目格式错误");
                if (!element.TryGetProperty("p", out var p) || !element.TryGetProperty("d", out var d) ||
                    !element.TryGetProperty("s", out var s) || !element.TryGetProperty("c", out var c))
                {
                    throw new InvalidDataException("交付清单条目缺少必需字段");
                }
                manifest.Items.Add(new ManifestItem
                {
                    Path = p.GetString() ?? "",
                    IsDirectory = d.GetInt32() != 0,
                    Size = s.GetInt64(),
                    Crc = unchecked((uint)c.GetInt64())
                });
            }

            if (manifest.ItemCount < 0 || manifest.ItemCount != manifest.Items.Count)
            {
                throw new InvalidDataException("交付清单条目数与 item_count 不一致");
            }
            return manifest;
        }
    }

    public enum VerifyMode
    {
        /// <summary>逐项核对路径、类型与长度（提交前的常规门槛）。</summary>
        Structure,
        /// <summary>额外逐文件核对 CRC32 摘要（回收源包前的强门槛）。</summary>
        StructureAndDigest
    }

    public sealed class VerifyReport
    {
        public List<string> Problems { get; } = new();
        public bool Ok => Problems.Count == 0;
        public int Files { get; set; }
        public int Directories { get; set; }
        public long TotalBytes { get; set; }
        public int DigestsChecked { get; set; }

        public string Describe()
        {
            if (Ok) return $"已逐项核对 {Files} 个文件、{Directories} 个目录，共 {TotalBytes} 字节";
            return string.Join("；", Problems.Take(5)) + (Problems.Count > 5 ? $"（共 {Problems.Count} 项问题）" : "");
        }
    }

    /// <summary>
    /// 交付树对账：把 Worker 的期望清单与最终目录逐项比对。
    /// 不做“只要目录非空就算成功”的弱判定，也不允许任何清单外的多余产物。
    /// </summary>
    public static class DeliveryVerifier
    {
        private const int MaxItems = 5_000_000;

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        public static VerifyReport Verify(string deliveryRootAbsolute, DeliveryManifest manifest, VerifyMode mode,
                                          Action<string>? log = null)
        {
            var report = new VerifyReport();
            if (!Directory.Exists(deliveryRootAbsolute))
            {
                report.Problems.Add("交付根目录不存在");
                return report;
            }
            if (manifest.Items.Count > MaxItems)
            {
                report.Problems.Add($"交付条目数量超出上限（{manifest.Items.Count}）");
                return report;
            }

            // 期望集合
            var expected = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
            var expectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Items)
            {
                string normalized = NormalizeRelative(item.Path);
                if (normalized.Length == 0 || IsUnsafeRelative(normalized))
                {
                    report.Problems.Add($"交付清单包含非法路径：{item.Path}");
                    continue;
                }
                if (expected.TryGetValue(normalized, out var existing))
                {
                    // 区分“源归档条目”与“最终文件系统节点”：同一实际目录可能因归档内
                    // 重复声明（如 folder/ 出现两次）而重复出现在清单中，此时应合并；
                    // 文件重名、大小写冲突、文件/目录类型冲突仍严格拒绝，不能删除重复项检查。
                    if (existing.IsDirectory && item.IsDirectory &&
                        string.Equals(existing.Path, item.Path, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    report.Problems.Add($"交付清单包含重复路径：{normalized}");
                    continue;
                }
                expected.Add(normalized, item);
                // 登记隐式父目录
                int slash = normalized.LastIndexOf('\\');
                while (slash > 0)
                {
                    expectedDirs.Add(normalized.Substring(0, slash));
                    slash = normalized.LastIndexOf('\\', slash - 1);
                }
                if (item.IsDirectory) expectedDirs.Add(normalized);
            }
            if (report.Problems.Count > 0) return report;

            // 先以句柄取回入口目录的真实最终路径：若入口路径本身或其任意祖先被替换为
            // 联接点/符号链接，内核解析出的最终路径会与字面规范路径不一致，从而在此处整体拒绝；
            // 后续遍历对每个条目再做一次同样的句柄级最终路径核对，因此即使根目录在校验后
            // 被并发换成联接点，枚举/读取也会因为“解析路径 ≠ 预期物理路径”而失败，而不是
            // 顺着联接点越界对账或写盘。
            string expectedCanonical = Path.GetFullPath(deliveryRootAbsolute).TrimEnd('\\');
            using SafeFileHandle? rootHandle = OpenPinnedObject(deliveryRootAbsolute);
            if (rootHandle == null)
            {
                report.Problems.Add("无法打开交付根目录（可能已被替换为联接点/符号链接）");
                return report;
            }
            string? rootFinal = GetFinalPathName(rootHandle);
            if (rootFinal == null ||
                !string.Equals(rootFinal.TrimEnd('\\'), expectedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                report.Problems.Add("交付根目录或其祖先包含联接点/符号链接");
                return report;
            }

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            WalkVerifiedTree(rootHandle, rootFinal,
                onDirectory: rel =>
                {
                    if (!expectedDirs.Contains(rel))
                    {
                        report.Problems.Add($"交付树包含清单外的目录：{rel}");
                    }
                    seenDirs.Add(rel);
                },
                onFile: (fullPath, rel, handle) =>
                {
                    if (!expected.TryGetValue(rel, out var expectedItem) || expectedItem.IsDirectory)
                    {
                        report.Problems.Add($"交付树包含清单外的文件：{rel}");
                        return;
                    }
                    long length = RandomAccess.GetLength(handle);
                    if (length != expectedItem.Size)
                    {
                        report.Problems.Add($"文件长度不一致：{rel}（期望 {expectedItem.Size}，实际 {length}）");
                        return;
                    }
                    if (mode == VerifyMode.StructureAndDigest)
                    {
                        uint actualCrc;
                        try
                        {
                            actualCrc = Crc32.ComputeHandle(handle);
                        }
                        catch (Exception ex)
                        {
                            report.Problems.Add($"无法读取文件做摘要校验：{rel}（{ex.Message}）");
                            return;
                        }
                        if (actualCrc != expectedItem.Crc)
                        {
                            report.Problems.Add($"文件摘要不一致：{rel}（期望 {expectedItem.Crc:X8}，实际 {actualCrc:X8}）");
                            return;
                        }
                        report.DigestsChecked++;
                    }
                    seenFiles.Add(rel);
                    report.TotalBytes += expectedItem.Size;
                },
                onProblem: msg => report.Problems.Add(msg));

            foreach (var item in manifest.Items)
            {
                string normalized = NormalizeRelative(item.Path);
                if (item.IsDirectory)
                {
                    if (!seenDirs.Contains(normalized)) report.Problems.Add($"缺少应保留的目录：{normalized}");
                }
                else if (!seenFiles.Contains(normalized))
                {
                    report.Problems.Add($"缺少应交付的文件：{normalized}");
                }
            }

            report.Files = seenFiles.Count;
            report.Directories = seenDirs.Count;
            if (log != null && report.Ok)
            {
                log($"校验通过：{report.Files} 个文件 / {report.Directories} 个目录 / {report.TotalBytes} 字节");
            }
            return report;
        }

        /// <summary>
        /// 打开一个目录（或文件）对象的句柄（不带 FILE_FLAG_OPEN_REPARSE_POINT，以便通过句柄的
        /// 最终路径识别入口本身或其祖先是否为联接点/符号链接：真实最终路径会与字面规范路径不一致）。
        /// 返回的句柄用于 <see cref="GetFinalPathName"/> 做归属核对；对文件而言，不授予
        /// FILE_SHARE_DELETE 且请求 GENERIC_READ 才能阻止其在校验期间被改名/替换。
        /// </summary>
        public static SafeFileHandle? OpenPinnedObject(string path)
        {
            SafeFileHandle handle = CreateFileW(path, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            return null;
        }

        /// <summary>取回句柄指向对象的真实最终路径（已解析全部重解析点，无尾反斜杠、无 \\?\ 前缀）。</summary>
        public static string? GetFinalPathName(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid) return null;
            uint capacity = 512;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var sb = new StringBuilder((int)capacity);
                uint len = GetFinalPathNameByHandleW(handle, sb, capacity, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
                if (len == 0) return null;
                if (len < capacity)
                {
                    string path = sb.ToString(0, (int)len).TrimEnd('\\');
                    // GetFinalPathNameByHandleW 以 FILE_NAME_NORMALIZED 返回时带 \\?\ 前缀，剥掉后再做归属比较。
                    if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        return @"\\" + path.Substring(8);
                    }
                    if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    {
                        return path.Substring(4);
                    }
                    return path;
                }
                capacity = len + 1;
            }
            return null;
        }

        /// <summary>
        /// 从已核对过真实最终路径的根目录句柄出发，安全遍历整棵交付树。
        /// 每个条目都以句柄取回真实最终路径并与其预期物理路径比对，任何联接点/符号链接
        /// （或被替换的对象）都会触发 onProblem 而不是被静默跟随；即使遍历期间父路径被
        /// 并发换成联接点，句柄解析出的真实路径也会与预期不一致而被拒绝。
        /// onFile 回调期间对象与祖先保持钉住；内容读取使用传入句柄，路径不得延迟使用。
        /// </summary>
        public static void WalkVerifiedTree(SafeFileHandle rootHandle, string rootPhysicalPath,
                                            Action<string> onDirectory,
                                            Action<string, string, SafeFileHandle> onFile,
                                            Action<string> onProblem)
        {
            var stack = new Stack<(string Physical, SafeFileHandle Handle, bool Exit)>();
            var owned = new HashSet<SafeFileHandle>();
            stack.Push((rootPhysicalPath.TrimEnd('\\'), rootHandle, false));
            try
            {
                while (stack.Count > 0)
                {
                    var (dirPhysical, dirHandle, exit) = stack.Pop();
                    if (exit)
                    {
                        if (owned.Remove(dirHandle)) dirHandle.Dispose();
                        continue;
                    }
                    // 子目录处理完毕后才释放父目录，保持整个祖先链钉住。
                    stack.Push((dirPhysical, dirHandle, true));
                    List<string> entries;
                    try { entries = Directory.EnumerateFileSystemEntries(dirPhysical).ToList(); }
                    catch (Exception ex)
                    {
                        onProblem($"无法枚举目录 {RelFrom(rootPhysicalPath, dirPhysical)}：{ex.Message}");
                        continue;
                    }
                    foreach (string full in entries)
                    {
                        string rel = RelFrom(rootPhysicalPath, full);
                        SafeFileHandle? entryHandle = OpenPinnedObject(full);
                        if (entryHandle == null)
                        {
                            onProblem($"无法打开条目 {rel}");
                            continue;
                        }
                        bool transferred = false;
                        try
                        {
                            string? finalEntry = GetFinalPathName(entryHandle);
                            if (finalEntry == null || !TryGetAttributes(entryHandle, out uint attributes) ||
                                !string.Equals(finalEntry.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                                (attributes & 0x400 /* FILE_ATTRIBUTE_REPARSE_POINT */) != 0)
                            {
                                onProblem($"交付树包含非预期的联接点/符号链接：{rel}");
                                continue;
                            }
                            if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                onDirectory(rel);
                                owned.Add(entryHandle);
                                transferred = true;
                                stack.Push((full, entryHandle, false));
                            }
                            else { onFile(full, rel, entryHandle); }
                        }
                        finally { if (!transferred) entryHandle.Dispose(); }
                    }
                }
            }
            finally
            {
                // 包括任意回调异常时仍在栈中的所有目录，不依赖 GC 回收原生句柄。
                foreach (var handle in owned) handle.Dispose();
            }
        }

        /// <summary>
        /// 判断完整路径本身或任意一级祖先是否为联接点/符号链接（reparse point）。
        /// 该轻量预检仅供诊断使用；Verify / ZoneIdentifier 的权威边界校验以句柄钉住
        /// （<see cref="OpenPinnedObject"/>）与真实最终路径比对为准，不再依赖本方法。
        /// </summary>
        public static bool PathContainsReparsePoint(string fullPath)
        {
            try
            {
                string current = Path.GetFullPath(fullPath);
                while (!string.IsNullOrEmpty(current))
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(current);
                    }
                    catch (Exception)
                    {
                        // 任一级无法读取属性即按存在越界风险处理（fail-closed）。
                        return true;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                    string? parent = Path.GetDirectoryName(current);
                    if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    current = parent;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        public static string NormalizeRelative(string relative)
        {
            // 只归一化路径分隔符与目录尾分隔符，绝不删除名称中的有效字符
            // （合法前导/尾随空格必须逐字保留，例如 " leading.txt"）。
            string norm = (relative ?? "").Replace('/', '\\');
            while (norm.EndsWith("\\", StringComparison.Ordinal)) norm = norm.Substring(0, norm.Length - 1);
            return norm;
        }

        private static bool IsUnsafeRelative(string normalized)
        {
            if (Path.IsPathRooted(normalized)) return true;
            if (normalized.Contains(':')) return true;
            foreach (string segment in normalized.Split('\\'))
            {
                if (segment == ".." || segment == "." || segment.Length == 0) return true;
            }
            return false;
        }

        private static string RelFrom(string rootPhysical, string fullPath)
        {
            string normalizedRoot = rootPhysical.TrimEnd('\\');
            if (fullPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(normalizedRoot.Length + 1);
            }
            return Path.GetFileName(fullPath);
        }

        private static bool TryGetAttributes(SafeFileHandle handle, out uint attributes)
        {
            attributes = 0;
            if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
            {
                attributes = info.dwFileAttributes;
                return true;
            }
            return false;
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
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
                                                         IntPtr lpSecurityAttributes, uint dwCreationDisposition,
                                                         uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath,
                                                             uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
                                                              out BY_HANDLE_FILE_INFORMATION lpFileInformation);
    }
}
